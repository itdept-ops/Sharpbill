"""Server-side session tracking (per device), keyed on the token's jti.

A cookie carries a random jti; a matching row in `user_sessions` must exist and not be revoked for
the session to be valid. Revoking one row signs out one device; revoking all rows for a user is the
"kick" / force-sign-out-everywhere path.
"""

import uuid
from datetime import UTC, datetime

from fastapi import Request
from sqlalchemy import select, update
from sqlalchemy.orm import Session

from app.auth.jwt import create_session_token
from app.models import UserSession


def _now() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def start_session(db: Session, user_id: int, request: Request) -> str:
    """Create a session row for a fresh login and return the cookie token bound to it."""
    jti = uuid.uuid4().hex
    ua = request.headers.get("user-agent")
    ip = request.client.host if request.client else None
    db.add(UserSession(user_id=user_id, jti=jti, user_agent=(ua[:400] if ua else None), ip=ip))
    db.commit()
    return create_session_token(user_id, jti)


def active_session(db: Session, jti: str) -> UserSession | None:
    """Return the live (non-revoked) session for this jti, or None."""
    session = db.scalar(select(UserSession).where(UserSession.jti == jti))
    if session is None or session.revoked_at is not None:
        return None
    return session


def revoke_session(session: UserSession, db: Session) -> None:
    session.revoked_at = _now()
    db.commit()


def revoke_all_for_user(db: Session, user_id: int) -> None:
    """Revoke every live session for a user (force sign-out everywhere)."""
    db.execute(
        update(UserSession)
        .where(UserSession.user_id == user_id, UserSession.revoked_at.is_(None))
        .values(revoked_at=_now())
    )
    db.commit()
