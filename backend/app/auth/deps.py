import jwt as pyjwt
from fastapi import Depends, Request
from sqlalchemy.orm import Session

from app.auth.jwt import COOKIE_NAME, decode_session_token
from app.db import get_db
from app.errors import ApiError
from app.models import User


def get_current_user(request: Request, db: Session = Depends(get_db)) -> User:
    token = request.cookies.get(COOKIE_NAME)
    if not token:
        raise ApiError(401, "NOT_AUTHENTICATED", "Not signed in")
    try:
        user_id = decode_session_token(token)
    except (pyjwt.InvalidTokenError, ValueError):
        raise ApiError(401, "INVALID_SESSION", "Session invalid or expired") from None

    user = db.get(User, user_id)  # DB read on every request, by design
    if user is None or not user.is_active:
        raise ApiError(401, "INVALID_SESSION", "Session invalid or expired")
    return user


def require_admin(user: User = Depends(get_current_user)) -> User:
    if user.role != "admin":
        raise ApiError(403, "FORBIDDEN", "Admin role required")
    return user
