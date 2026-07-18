from datetime import UTC, datetime

from fastapi import APIRouter, Depends, Request, Response
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.auth import ProviderTokenError
from app.auth.deps import get_current_user
from app.auth.google import verify_google_id_token
from app.auth.jwt import COOKIE_NAME, clear_session_cookie, decode_session_token, set_session_cookie
from app.auth.microsoft import verify_microsoft_id_token
from app.auth.nonce import issue_nonce
from app.auth.service import find_or_create_user
from app.auth.sessions import revoke_session, start_session
from app.config import settings
from app.db import get_db
from app.errors import ApiError
from app.geo import place_for, timezone_for
from app.models import SiteSettings, User, UserSession
from app.schemas.auth import AuthConfig, LocationUpdate, SessionOut, TokenLoginRequest
from app.schemas.user import UserOut


def _current_jti(request: Request) -> str | None:
    token = request.cookies.get(COOKIE_NAME)
    if not token:
        return None
    try:
        return decode_session_token(token)["jti"]
    except Exception:
        return None


# The login-CSRF guard (reject non-JSON Content-Type on these two routes) is enforced by
# middleware in app.main, which runs before body parsing.
router = APIRouter()


@router.get("/config", response_model=AuthConfig)
def auth_config(db: Session = Depends(get_db)) -> AuthConfig:
    site = db.get(SiteSettings, 1)
    return AuthConfig(
        google=bool(settings.google_client_id),
        microsoft=bool(settings.azure_client_id),
        dev=settings.is_dev_auth_enabled,
        calm=bool(site.calm_mode) if site else False,
    )


@router.get("/nonce")
def auth_nonce() -> dict:
    """Issue a single-use nonce for a provider sign-in. The SPA passes it into the OIDC request;
    the provider echoes it in the id_token, and the verifier consumes it exactly once."""
    return {"nonce": issue_nonce()}


@router.post("/google", response_model=UserOut)
def login_google(
    body: TokenLoginRequest, request: Request, response: Response, db: Session = Depends(get_db)
) -> UserOut:
    try:
        ident = verify_google_id_token(body.id_token)
    except ProviderTokenError:
        raise ApiError(401, "INVALID_TOKEN", "Invalid Google token") from None
    user = find_or_create_user(db, ident)
    set_session_cookie(response, start_session(db, user.id, request))
    return UserOut.from_user(user, online=True)


@router.post("/microsoft", response_model=UserOut)
def login_microsoft(
    body: TokenLoginRequest, request: Request, response: Response, db: Session = Depends(get_db)
) -> UserOut:
    try:
        ident = verify_microsoft_id_token(body.id_token)
    except ProviderTokenError:
        raise ApiError(401, "INVALID_TOKEN", "Invalid Microsoft token") from None
    user = find_or_create_user(db, ident)
    set_session_cookie(response, start_session(db, user.id, request))
    return UserOut.from_user(user, online=True)


@router.post("/logout", status_code=204)
def logout(request: Request, response: Response, db: Session = Depends(get_db)) -> Response:
    # Revoke THIS device's session server-side (so the just-cleared cookie is rejected if replayed),
    # leaving the user's other devices signed in. Logout always succeeds (204) regardless.
    jti = _current_jti(request)
    if jti:
        session = db.scalar(select(UserSession).where(UserSession.jti == jti))
        if session is not None and session.revoked_at is None:
            revoke_session(session, db)
    clear_session_cookie(response)
    response.status_code = 204
    return response


@router.get("/me", response_model=UserOut)
def me(user: User = Depends(get_current_user)) -> UserOut:
    return UserOut.from_user(user, online=True)


@router.get("/sessions", response_model=list[SessionOut])
def my_sessions(
    request: Request, db: Session = Depends(get_db), user: User = Depends(get_current_user)
) -> list[SessionOut]:
    """The current user's active sessions (one per signed-in device)."""
    current = _current_jti(request)
    rows = db.scalars(
        select(UserSession)
        .where(UserSession.user_id == user.id, UserSession.revoked_at.is_(None))
        .order_by(UserSession.created_at.desc())
    )
    return [SessionOut.from_row(s, current=s.jti == current) for s in rows]


@router.delete("/sessions/{session_id}", status_code=204)
def revoke_my_session(
    session_id: int,
    response: Response,
    db: Session = Depends(get_db),
    user: User = Depends(get_current_user),
) -> Response:
    """Sign out one of your own devices."""
    session = db.get(UserSession, session_id)
    if session is None or session.user_id != user.id:
        raise ApiError(404, "NOT_FOUND", "Session not found")
    if session.revoked_at is None:
        revoke_session(session, db)
    response.status_code = 204
    return response


@router.post("/location", status_code=204)
def update_location(
    body: LocationUpdate,
    response: Response,
    db: Session = Depends(get_db),
    user: User = Depends(get_current_user),
) -> Response:
    """Store the user's opt-in location, and derive their place + timezone from it (offline).

    The frontend only calls this if the user grants location access.
    """
    user.last_latitude = body.latitude
    user.last_longitude = body.longitude
    user.last_location_accuracy = body.accuracy
    user.last_location_at = datetime.now(UTC).replace(tzinfo=None)

    # Fill in location + timezone from the coordinates (offline reverse-geocode).
    place = place_for(body.latitude, body.longitude)
    tz = timezone_for(body.latitude, body.longitude)
    if place:
        user.location = place
    if tz:
        user.timezone = tz

    db.commit()
    response.status_code = 204
    return response
