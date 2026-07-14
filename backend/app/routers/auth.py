from fastapi import APIRouter, Depends, Response
from sqlalchemy.orm import Session

from app.auth import ProviderTokenError
from app.auth.deps import get_current_user
from app.auth.google import verify_google_id_token
from app.auth.jwt import clear_session_cookie, create_session_token, set_session_cookie
from app.auth.microsoft import verify_microsoft_id_token
from app.auth.service import find_or_create_user
from app.config import settings
from app.db import get_db
from app.errors import ApiError
from app.models import User
from app.schemas.auth import AuthConfig, TokenLoginRequest
from app.schemas.user import UserOut

# The login-CSRF guard (reject non-JSON Content-Type on these two routes) is enforced by
# middleware in app.main, which runs before body parsing.
router = APIRouter()


@router.get("/config", response_model=AuthConfig)
def auth_config() -> AuthConfig:
    return AuthConfig(
        google=bool(settings.google_client_id),
        microsoft=bool(settings.azure_client_id),
        dev=settings.is_dev_auth_enabled,
    )


@router.post("/google", response_model=UserOut)
def login_google(
    body: TokenLoginRequest, response: Response, db: Session = Depends(get_db)
) -> UserOut:
    try:
        ident = verify_google_id_token(body.id_token)
    except ProviderTokenError:
        raise ApiError(401, "INVALID_TOKEN", "Invalid Google token") from None
    user = find_or_create_user(db, ident)
    set_session_cookie(response, create_session_token(user.id))
    return UserOut.from_user(user)


@router.post("/microsoft", response_model=UserOut)
def login_microsoft(
    body: TokenLoginRequest, response: Response, db: Session = Depends(get_db)
) -> UserOut:
    try:
        ident = verify_microsoft_id_token(body.id_token)
    except ProviderTokenError:
        raise ApiError(401, "INVALID_TOKEN", "Invalid Microsoft token") from None
    user = find_or_create_user(db, ident)
    set_session_cookie(response, create_session_token(user.id))
    return UserOut.from_user(user)


@router.post("/logout", status_code=204)
def logout(response: Response) -> Response:
    clear_session_cookie(response)
    response.status_code = 204
    return response


@router.get("/me", response_model=UserOut)
def me(user: User = Depends(get_current_user)) -> UserOut:
    return UserOut.from_user(user)
