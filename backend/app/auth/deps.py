from collections.abc import Callable
from datetime import UTC, datetime

import jwt as pyjwt
from fastapi import Depends, Request
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.account_lifecycle import account_is_authenticatable, lock_current_user
from app.auth.jwt import COOKIE_NAME, decode_session_token
from app.auth.sessions import active_session
from app.db import get_db
from app.errors import ApiError
from app.models import User, UserSession

# How stale last_seen may be before we bump it (throttles presence writes).
_PRESENCE_REFRESH_SECONDS = 15


def _utcnow_naive() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def _touch(db: Session, user: User, session: UserSession) -> User | None:
    """Recheck lifecycle/session state under current locks before touching presence metadata."""
    now = _utcnow_naive()
    current_user = lock_current_user(db, user.id)
    if current_user is None or not account_is_authenticatable(current_user):
        db.rollback()
        return None
    current_session = db.scalar(
        select(UserSession)
        .where(UserSession.id == session.id, UserSession.user_id == current_user.id)
        .with_for_update()
        .execution_options(populate_existing=True)
    )
    if (
        current_session is None
        or current_session.revoked_at is not None
        or current_session.expires_at <= now
    ):
        db.rollback()
        return None
    stale = lambda ts: ts is None or (now - ts).total_seconds() > _PRESENCE_REFRESH_SECONDS  # noqa: E731
    if stale(current_user.last_seen_at):
        current_user.last_seen_at = now
    if stale(current_session.last_seen_at):
        current_session.last_seen_at = now
    # Commit even when neither timestamp changed, releasing the lifecycle/session locks before
    # the route executes. Lifecycle-sensitive mutations take a fresh current lock themselves.
    db.commit()
    return current_user


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
    session = active_session(db, payload["jti"], user.id)
    if session is None:
        raise ApiError(401, "SESSION_REVOKED", "This session was signed out")

    # Stash the resolved principal so the request-logging middleware can reuse it instead of
    # decoding the JWT a second time on the way out.
    current_user = _touch(db, user, session)
    if current_user is None:
        raise ApiError(401, "INVALID_SESSION", "Session invalid or expired")
    request.state.user_id = current_user.id
    return current_user


def require_permission(key: str) -> Callable[..., User]:
    """Dependency factory: asserts the current user's role grants `key`, returns the user."""

    def dependency(user: User = Depends(get_current_user)) -> User:
        if key not in user.permission_keys:
            raise ApiError(403, "FORBIDDEN", f"Missing permission: {key}")
        return user

    return dependency
