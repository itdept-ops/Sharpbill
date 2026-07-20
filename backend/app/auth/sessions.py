"""Server-side session tracking (per device), keyed on the token's jti.

A cookie carries a random jti; a matching row in `user_sessions` must exist and not be revoked for
the session to be valid. Revoking one row signs out one device; revoking all rows for a user is the
"kick" / force-sign-out-everywhere path.
"""

import uuid
from datetime import UTC, datetime, timedelta

from fastapi import Request
from sqlalchemy import delete, select, update
from sqlalchemy.orm import Session

from app.auth.jwt import create_session_token
from app.config import settings
from app.models import User, UserSession

_CLEANUP_BATCH_SIZE = 500


def _now() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def prune_stale_sessions(
    db: Session, *, now: datetime | None = None, limit: int | None = None
) -> int:
    """Delete a bounded batch only after expiry/revocation metadata exceeds retention."""
    current = now or _now()
    batch_size = limit or settings.session_prune_batch_size
    retention_cutoff = current - timedelta(days=settings.session_retention_days)
    stale_ids = list(
        db.scalars(
            select(UserSession.id)
            .where(UserSession.expires_at <= retention_cutoff)
            .order_by(UserSession.expires_at, UserSession.id)
            .limit(batch_size)
        )
    )
    remaining = batch_size - len(stale_ids)
    if remaining > 0:
        revoked_query = (
            select(UserSession.id)
            .where(UserSession.revoked_at <= retention_cutoff)
            .order_by(UserSession.revoked_at, UserSession.id)
            .limit(remaining)
        )
        if stale_ids:
            revoked_query = revoked_query.where(UserSession.id.not_in(stale_ids))
        stale_ids.extend(db.scalars(revoked_query))
    if stale_ids:
        db.execute(delete(UserSession).where(UserSession.id.in_(stale_ids)))
    return len(stale_ids)


def _enforce_concurrent_session_cap(db: Session, user_id: int, now: datetime) -> None:
    """Revoke oldest live sessions before adding one, preserving a hard per-user ceiling."""
    active_filter = (
        UserSession.user_id == user_id,
        UserSession.revoked_at.is_(None),
        UserSession.expires_at > now,
    )
    probe_limit = settings.max_active_sessions_per_user + _CLEANUP_BATCH_SIZE + 1
    # A locking read sees the latest committed rows even under MySQL REPEATABLE READ. Combined
    # with the user-row lock in start_session, this closes the concurrent-login cap race.
    active_ids = list(
        db.scalars(
            select(UserSession.id)
            .where(*active_filter)
            .order_by(UserSession.created_at, UserSession.id)
            .limit(probe_limit)
            .with_for_update()
        )
    )
    revoke_count = len(active_ids) - settings.max_active_sessions_per_user + 1
    if revoke_count <= 0:
        return
    # Under the enforced invariant this is normally one row. If a legacy/corrupt account exceeds
    # the bounded probe, revoke all live rows in one recovery statement instead of materializing
    # an attacker-controlled number of IDs.
    if len(active_ids) == probe_limit:
        db.execute(update(UserSession).where(*active_filter).values(revoked_at=now))
        return
    oldest_ids = active_ids[:revoke_count]
    if oldest_ids:
        db.execute(update(UserSession).where(UserSession.id.in_(oldest_ids)).values(revoked_at=now))


def start_session(db: Session, user_id: int, request: Request) -> str:
    """Create a session row for a fresh login and return the cookie token bound to it."""
    prune_stale_sessions(db)
    now = _now()
    # Serialize concurrent logins for one account so two requests cannot both observe spare
    # capacity and exceed the per-user ceiling.
    db.scalar(select(User.id).where(User.id == user_id).with_for_update())
    _enforce_concurrent_session_cap(db, user_id, now)
    jti = uuid.uuid4().hex
    ua = request.headers.get("user-agent")
    ip = request.client.host if request.client else None
    db.add(UserSession(user_id=user_id, jti=jti, user_agent=(ua[:400] if ua else None), ip=ip))
    db.commit()
    return create_session_token(user_id, jti)


def active_session(db: Session, jti: str, user_id: int) -> UserSession | None:
    """Return the live session only when both token claims bind to the same server row."""
    return db.scalar(
        select(UserSession).where(
            UserSession.jti == jti,
            UserSession.user_id == user_id,
            UserSession.revoked_at.is_(None),
            UserSession.expires_at > _now(),
        )
    )


def revoke_session(session: UserSession, db: Session, *, commit: bool = True) -> None:
    session.revoked_at = _now()
    if commit:
        db.commit()


def revoke_all_for_user(db: Session, user_id: int, *, commit: bool = True) -> None:
    """Revoke every live session for a user (force sign-out everywhere)."""
    db.execute(
        update(UserSession)
        .where(UserSession.user_id == user_id, UserSession.revoked_at.is_(None))
        .values(revoked_at=_now())
    )
    if commit:
        db.commit()
