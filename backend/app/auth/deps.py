from collections.abc import Callable
from datetime import UTC, datetime

import jwt as pyjwt
from fastapi import Depends, Request
from sqlalchemy.orm import Session

from app.auth.jwt import COOKIE_NAME, decode_session_token
from app.db import get_db
from app.errors import ApiError
from app.models import User

# How stale last_seen may be before we bump it (throttles presence writes).
_PRESENCE_REFRESH_SECONDS = 15


def _utcnow_naive() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def _touch_last_seen(db: Session, user: User) -> None:
    now = _utcnow_naive()
    if (
        user.last_seen_at is None
        or (now - user.last_seen_at).total_seconds() > _PRESENCE_REFRESH_SECONDS
    ):
        user.last_seen_at = now
        db.commit()


def get_current_user(request: Request, db: Session = Depends(get_db)) -> User:
    token = request.cookies.get(COOKIE_NAME)
    if not token:
        raise ApiError(401, "NOT_AUTHENTICATED", "Not signed in")
    try:
        payload = decode_session_token(token)
    except (pyjwt.InvalidTokenError, ValueError, KeyError):
        raise ApiError(401, "INVALID_SESSION", "Session invalid or expired") from None

    user = db.get(User, int(payload["sub"]))  # DB read every request, by design
    if user is None or not user.is_active or not user.is_approved:
        raise ApiError(401, "INVALID_SESSION", "Session invalid or expired")

    # Session kill-switch (kick): reject tokens minted at or before the cutoff second.
    # JWT `iat` is whole-second; comparing against the floored cutoff avoids a same-second
    # race (a token from the kick's second is revoked; re-login the next second succeeds).
    if user.session_valid_after is not None:
        issued_at = int(payload.get("iat", 0))
        cutoff = int(user.session_valid_after.replace(tzinfo=UTC).timestamp())
        if issued_at <= cutoff:
            raise ApiError(401, "SESSION_REVOKED", "Your session was ended by an administrator")

    _touch_last_seen(db, user)
    return user


def require_permission(key: str) -> Callable[..., User]:
    """Dependency factory: asserts the current user's role grants `key`, returns the user."""

    def dependency(user: User = Depends(get_current_user)) -> User:
        if key not in user.permission_keys:
            raise ApiError(403, "FORBIDDEN", f"Missing permission: {key}")
        return user

    return dependency
