"""Dev-only login. This router is mounted by main.py ONLY when settings.is_dev_auth_enabled
(i.e. APP_ENV=local AND DEV_AUTH_ENABLED=true). It is never registered otherwise.
"""

from fastapi import APIRouter, Depends, Response
from sqlalchemy.orm import Session

from app.auth.jwt import create_session_token, set_session_cookie
from app.auth.service import dev_upsert_user
from app.db import get_db
from app.errors import ApiError
from app.schemas.auth import DevLoginRequest
from app.schemas.user import UserOut

router = APIRouter()


@router.post("/dev", response_model=UserOut)
def dev_login(body: DevLoginRequest, response: Response, db: Session = Depends(get_db)) -> UserOut:
    user = dev_upsert_user(db, str(body.email), body.role, body.display_name)
    if not user.is_active:
        raise ApiError(403, "ACCOUNT_DISABLED", "This account has been deactivated")
    set_session_cookie(response, create_session_token(user.id))
    return UserOut.from_user(user, online=True)
