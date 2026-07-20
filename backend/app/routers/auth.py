import json
import logging
from datetime import UTC, datetime
from typing import Literal

from fastapi import APIRouter, Depends, Request, Response
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.account_lifecycle import account_is_authenticatable, lock_current_user
from app.auth import ProviderTokenError, ProviderUnavailableError
from app.auth.deps import get_current_user
from app.auth.google import verify_google_id_token
from app.auth.jwt import COOKIE_NAME, clear_session_cookie, decode_session_token, set_session_cookie
from app.auth.microsoft import verify_microsoft_id_token
from app.auth.nonce import NonceCapacityError, issue_nonce
from app.auth.service import find_or_create_user
from app.auth.sessions import SessionPrincipalUnavailable, revoke_session, start_session
from app.config import settings
from app.db import get_db
from app.errors import ApiError
from app.geo import place_for, timezone_for
from app.models import SiteSettings, User, UserSession
from app.schemas.auth import AuthConfig, LocationUpdate, SessionOut, TokenLoginRequest
from app.schemas.user import UserOut
from app.security_events import SecurityOutcome, add_security_event, commit_security_event


def _current_session_claims(request: Request) -> dict | None:
    token = request.cookies.get(COOKIE_NAME)
    if not token:
        return None
    try:
        return decode_session_token(token)
    except Exception:
        return None


# The login-CSRF guard (reject non-JSON Content-Type on these two routes) is enforced by
# middleware in app.main, which runs before body parsing.
router = APIRouter()
_security_log = logging.getLogger("app.security")


def _audit_login_failure(
    db: Session,
    request: Request,
    *,
    provider: str,
    reason: str,
    outcome: SecurityOutcome = "denied",
) -> None:
    # A failed business transaction must not poison the independent evidence insert.
    db.rollback()
    try:
        commit_security_event(
            db,
            event_type="auth.login",
            outcome=outcome,
            severity="warning",
            request=request,
            target_type="identity_provider",
            target_id=provider,
            metadata={"provider": provider, "reason": reason},
        )
    except Exception:
        # Evidence failure must be visible, but it must not turn a deliberate 401/403/503 into
        # an unrelated 500 or leak provider input through an exception response.
        db.rollback()
        _security_log.exception(
            "%s",
            json.dumps(
                {
                    "event": "security_event_persist_failed",
                    "security_event_type": "auth.login",
                    "request_id": getattr(request.state, "request_id", None),
                    "provider": provider,
                    "outcome": outcome,
                },
                separators=(",", ":"),
            ),
        )


def _assert_provider_login_enabled(
    db: Session,
    request: Request,
    provider: Literal["google", "microsoft"],
) -> None:
    """Reject disabled/unconfigured providers before token verification can perform network I/O."""
    site = db.get(SiteSettings, 1)
    configured = (
        settings.google_provider_configured
        if provider == "google"
        else settings.microsoft_provider_configured
    )
    allowed = bool(
        site
        and (site.allow_google if provider == "google" else site.allow_microsoft)
        and configured
    )
    if allowed:
        return

    _audit_login_failure(db, request, provider=provider, reason="PROVIDER_DISABLED")
    display_name = "Google" if provider == "google" else "Microsoft"
    raise ApiError(403, "PROVIDER_DISABLED", f"{display_name} sign-in is currently disabled")


@router.get("/config", response_model=AuthConfig)
def auth_config(db: Session = Depends(get_db)) -> AuthConfig:
    site = db.get(SiteSettings, 1)
    google = settings.google_provider_configured and bool(site and site.allow_google)
    microsoft = settings.microsoft_provider_configured and bool(site and site.allow_microsoft)
    return AuthConfig(
        google=google,
        microsoft=microsoft,
        google_client_id=settings.google_client_id if google else None,
        microsoft_client_id=settings.azure_client_id if microsoft else None,
        dev=settings.is_dev_auth_enabled,
        calm=bool(site.calm_mode) if site else False,
    )


@router.post("/nonce", status_code=201)
def auth_nonce() -> dict:
    """Issue a single-use nonce for a provider sign-in. The SPA passes it into the OIDC request;
    the provider echoes it in the id_token, and the verifier consumes it exactly once."""
    try:
        return {"nonce": issue_nonce()}
    except NonceCapacityError:
        raise ApiError(
            503,
            "LOGIN_STATE_CAPACITY",
            "Sign-in is temporarily at capacity; retry shortly",
            headers={"Retry-After": "30"},
        ) from None


@router.post("/google", response_model=UserOut)
def login_google(
    body: TokenLoginRequest, request: Request, response: Response, db: Session = Depends(get_db)
) -> UserOut:
    _assert_provider_login_enabled(db, request, "google")
    try:
        ident = verify_google_id_token(body.id_token)
    except ProviderUnavailableError:
        _audit_login_failure(
            db, request, provider="google", reason="PROVIDER_UNAVAILABLE", outcome="failure"
        )
        raise ApiError(
            503, "PROVIDER_UNAVAILABLE", "Google sign-in is temporarily unavailable"
        ) from None
    except ProviderTokenError:
        _audit_login_failure(db, request, provider="google", reason="INVALID_TOKEN")
        raise ApiError(401, "INVALID_TOKEN", "Invalid Google token") from None
    try:
        user = find_or_create_user(db, ident)
    except ApiError as exc:
        _audit_login_failure(db, request, provider="google", reason=exc.code)
        raise
    request.state.user_id = user.id
    add_security_event(
        db,
        event_type="auth.login",
        outcome="success",
        request=request,
        actor_user_id=user.id,
        target_type="user",
        target_id=user.id,
        metadata={"provider": "google"},
    )
    try:
        token = start_session(db, user.id, request)
    except SessionPrincipalUnavailable as exc:
        _audit_login_failure(db, request, provider="google", reason=exc.code)
        raise ApiError(403, exc.code, exc.message) from None
    set_session_cookie(response, token)
    return UserOut.from_user(user, online=True, include_identity_subjects=True)


@router.post("/microsoft", response_model=UserOut)
def login_microsoft(
    body: TokenLoginRequest, request: Request, response: Response, db: Session = Depends(get_db)
) -> UserOut:
    _assert_provider_login_enabled(db, request, "microsoft")
    try:
        ident = verify_microsoft_id_token(body.id_token)
    except ProviderUnavailableError:
        _audit_login_failure(
            db, request, provider="microsoft", reason="PROVIDER_UNAVAILABLE", outcome="failure"
        )
        raise ApiError(
            503, "PROVIDER_UNAVAILABLE", "Microsoft sign-in is temporarily unavailable"
        ) from None
    except ProviderTokenError:
        _audit_login_failure(db, request, provider="microsoft", reason="INVALID_TOKEN")
        raise ApiError(401, "INVALID_TOKEN", "Invalid Microsoft token") from None
    try:
        user = find_or_create_user(db, ident)
    except ApiError as exc:
        _audit_login_failure(db, request, provider="microsoft", reason=exc.code)
        raise
    request.state.user_id = user.id
    add_security_event(
        db,
        event_type="auth.login",
        outcome="success",
        request=request,
        actor_user_id=user.id,
        target_type="user",
        target_id=user.id,
        metadata={"provider": "microsoft"},
    )
    try:
        token = start_session(db, user.id, request)
    except SessionPrincipalUnavailable as exc:
        _audit_login_failure(db, request, provider="microsoft", reason=exc.code)
        raise ApiError(403, exc.code, exc.message) from None
    set_session_cookie(response, token)
    return UserOut.from_user(user, online=True, include_identity_subjects=True)


@router.post("/logout", status_code=204)
def logout(request: Request, response: Response, db: Session = Depends(get_db)) -> Response:
    # Revoke THIS device's session server-side (so the just-cleared cookie is rejected if replayed),
    # leaving the user's other devices signed in. Logout always succeeds (204) regardless.
    claims = _current_session_claims(request)
    if claims:
        try:
            user_id = int(claims["sub"])
            jti = str(claims["jti"])
        except (KeyError, TypeError, ValueError):
            user_id = 0
            jti = ""
        session = db.scalar(
            select(UserSession).where(UserSession.jti == jti, UserSession.user_id == user_id)
        )
        revoked = revoke_session(session, db, commit=False) if session is not None else False
        add_security_event(
            db,
            event_type="auth.logout",
            outcome="success",
            request=request,
            actor_user_id=user_id,
            target_type="user",
            target_id=user_id,
            metadata={"session_revoked": revoked},
        )
        db.commit()
    clear_session_cookie(response)
    response.status_code = 204
    return response


@router.get("/me", response_model=UserOut)
def me(user: User = Depends(get_current_user)) -> UserOut:
    return UserOut.from_user(user, online=True, include_identity_subjects=True)


@router.get("/sessions", response_model=list[SessionOut])
def my_sessions(
    request: Request, db: Session = Depends(get_db), user: User = Depends(get_current_user)
) -> list[SessionOut]:
    """The current user's active sessions (one per signed-in device)."""
    claims = _current_session_claims(request)
    current = str(claims["jti"]) if claims else None
    rows = db.scalars(
        select(UserSession)
        .where(
            UserSession.user_id == user.id,
            UserSession.revoked_at.is_(None),
            UserSession.expires_at > datetime.now(UTC).replace(tzinfo=None),
        )
        .order_by(UserSession.created_at.desc())
    )
    return [SessionOut.from_row(s, current=s.jti == current) for s in rows]


@router.delete("/sessions/{session_id}", status_code=204)
def revoke_my_session(
    session_id: int,
    request: Request,
    response: Response,
    db: Session = Depends(get_db),
    user: User = Depends(get_current_user),
) -> Response:
    """Sign out one of your own devices."""
    session = db.get(UserSession, session_id)
    if session is None or session.user_id != user.id:
        raise ApiError(404, "NOT_FOUND", "Session not found")
    revoke_session(session, db, commit=False)
    add_security_event(
        db,
        event_type="session.revoked",
        outcome="success",
        request=request,
        actor_user_id=user.id,
        target_type="user_session",
        target_id=session.id,
        metadata={"scope": "self"},
    )
    db.commit()
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
    current_user = lock_current_user(db, user.id)
    if not account_is_authenticatable(current_user):
        db.rollback()
        raise ApiError(401, "INVALID_SESSION", "Session invalid or expired")
    assert current_user is not None
    user = current_user
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
