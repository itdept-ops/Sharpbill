from collections.abc import Callable
from datetime import UTC, datetime

import jwt as pyjwt
from fastapi import Depends, Request
from sqlalchemy.orm import Session

from app.auth.jwt import COOKIE_NAME, decode_session_token
from app.auth.sessions import active_session
from app.db import get_db
from app.errors import ApiError
from app.models import User, UserSession

# How stale last_seen may be before we bump it (throttles presence writes).
_PRESENCE_REFRESH_SECONDS = 15


def _utcnow_naive() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def _touch(db: Session, user: User, session: UserSession) -> None:
    now = _utcnow_naive()
    stale = lambda ts: ts is None or (now - ts).total_seconds() > _PRESENCE_REFRESH_SECONDS  # noqa: E731
    changed = False
    if stale(user.last_seen_at):
        user.last_seen_at = now
        changed = True
    if stale(session.last_seen_at):
        session.last_seen_at = now
        changed = True
    if changed:
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

    # Global kill-switch (kick/deactivate): reject tokens minted at or before the cutoff second.
    # JWT `iat` is whole-second; comparing against the floored cutoff avoids a same-second
    # race (a token from the kick's second is revoked; re-login the next second succeeds).
    if user.session_valid_after is not None:
        issued_at = int(payload.get("iat", 0))
        cutoff = int(user.session_valid_after.replace(tzinfo=UTC).timestamp())
        if issued_at <= cutoff:
            raise ApiError(401, "SESSION_REVOKED", "Your session was ended by an administrator")

    # Per-device revocation: the token's session row must still exist and be un-revoked.
    session = active_session(db, payload["jti"])
    if session is None:
        raise ApiError(401, "SESSION_REVOKED", "This session was signed out")

    _touch(db, user, session)
    return user


def require_permission(key: str) -> Callable[..., User]:
    """Dependency factory: asserts the current user's role grants `key`, returns the user."""

    def dependency(user: User = Depends(get_current_user)) -> User:
        if key not in user.permission_keys:
            raise ApiError(403, "FORBIDDEN", f"Missing permission: {key}")
        return user

    return dependency
