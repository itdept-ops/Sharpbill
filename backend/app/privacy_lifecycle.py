"""Hold-aware, bounded privacy lifecycle operations.

Every governed batch locks the singleton site-settings row before selecting data to remove. A
legal-hold change must take the same row lock, so the hold decision and the batch commit form one
serialization boundary. Login nonces are deliberately handled elsewhere: expired authentication
state is never evidence and must be removed even while a legal hold is active.

Account erasure is anonymization, not hard deletion. The disabled identity row remains as the
minimum suppression/bootstrap-consumption marker, while user profile data is removed.
"""

from datetime import UTC, datetime, timedelta
from typing import cast

from sqlalchemy import Table, case, delete, or_, select, update
from sqlalchemy.orm import Session, selectinload

from app.auth.sessions import prune_stale_sessions
from app.config import settings
from app.models import (
    LegalAcceptance,
    RequestLog,
    Role,
    SecurityEvent,
    SiteSettings,
    User,
    UserSession,
)
from app.permissions import ADMIN_ROLE, DEFAULT_ROLE
from app.security_events import add_security_event


class RetentionHoldActive(RuntimeError):
    """Raised when an explicit destructive privacy action is blocked by legal hold."""


class AccountErasureError(ValueError):
    """Raised when an account is not eligible for an erasure lifecycle transition."""


def _now() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def lock_retention_policy(db: Session) -> SiteSettings:
    """Lock and return the singleton policy row, failing closed if it is absent."""
    site = db.scalar(
        select(SiteSettings)
        .where(SiteSettings.id == 1)
        .with_for_update()
        # A long-lived caller may already have the singleton in its identity map. A locking read
        # is current in InnoDB, and populate_existing ensures that current value replaces cache.
        .execution_options(populate_existing=True)
    )
    if site is None:
        raise RuntimeError("site settings are missing; retention cannot make a safe hold decision")
    return site


def retention_hold_active(db: Session) -> bool:
    """Return the globally governed hold state while retaining the policy-row lock."""
    return bool(lock_retention_policy(db).retention_hold)


def retention_hold_enabled(db: Session, *, lock: bool = True) -> bool:
    """Stable route-facing hold query; destructive callers should retain the default lock."""
    if lock:
        return retention_hold_active(db)
    site = db.get(SiteSettings, 1)
    if site is None:
        raise RuntimeError("site settings are missing; retention hold state is unavailable")
    return bool(site.retention_hold)


def require_no_retention_hold(db: Session) -> None:
    """Guard an explicit user/admin deletion operation with the global legal hold."""
    if retention_hold_active(db):
        raise RetentionHoldActive("data deletion is suspended by an active legal hold")


def clear_precise_location(user: User) -> bool:
    """Remove saved coordinates/accuracy while leaving the coarse profile under user control."""
    changed = any(
        value is not None
        for value in (
            user.last_latitude,
            user.last_longitude,
            user.last_location_accuracy,
            user.last_location_at,
        )
    )
    user.last_latitude = None
    user.last_longitude = None
    user.last_location_accuracy = None
    user.last_location_at = None
    return changed


def clear_user_location(user: User) -> bool:
    """Clear both precise capture and its coarse derived profile at the user's request."""
    changed = clear_precise_location(user) or user.location is not None or user.timezone is not None
    user.location = None
    user.timezone = None
    return changed


def request_account_erasure(
    user: User, *, now: datetime | None = None, grace_days: int | None = None
) -> datetime:
    """Schedule a reversible erasure request; callers own authorization and commit."""
    if user.role_name == ADMIN_ROLE:
        raise AccountErasureError("administrator accounts cannot be scheduled for erasure")
    if user.erased_at is not None:
        raise AccountErasureError("the account is already erased")
    requested_at = now or _now()
    due_at = requested_at + timedelta(
        days=(settings.account_erasure_grace_days if grace_days is None else grace_days)
    )
    user.erasure_requested_at = requested_at
    user.erasure_due_at = due_at
    return due_at


def schedule_erasure(
    db: Session, user: User, *, now: datetime | None = None, grace_days: int | None = None
) -> datetime:
    """Stable route helper that schedules erasure under the legal-hold serialization lock."""
    require_no_retention_hold(db)
    # Every governed path takes the singleton policy lock before its account lock. Refreshing the
    # already-resolved target here closes request/cancel/worker races without reversing that order.
    db.refresh(user, with_for_update=True)
    return request_account_erasure(user, now=now, grace_days=grace_days)


def cancel_account_erasure(user: User) -> bool:
    """Cancel a pending request without altering account activation state."""
    if user.erased_at is not None:
        raise AccountErasureError("an erased account cannot be restored")
    changed = any(value is not None for value in (user.erasure_requested_at, user.erasure_due_at))
    user.erasure_requested_at = None
    user.erasure_due_at = None
    return changed


def cancel_erasure(user: User) -> bool:
    """Stable route helper for cancelling a request; legal hold does not forbid preservation."""
    return cancel_account_erasure(user)


def anonymize_user(
    db: Session,
    user: User,
    *,
    now: datetime | None = None,
    policy_trigger: str = "retention_policy",
    default_role: Role | None = None,
) -> None:
    """Irreversibly remove account PII while retaining a disabled identity suppression marker."""
    if user.role_name == ADMIN_ROLE:
        raise AccountErasureError("administrator accounts must never be automatically erased")
    if user.erased_at is not None:
        return
    current = now or _now()
    resolved_default_role = default_role or db.scalar(select(Role).where(Role.name == DEFAULT_ROLE))
    if resolved_default_role is None:
        raise RuntimeError("built-in user role is missing; account erasure refused")

    # Retain only non-identifying lifecycle and authorization-safe state on the user row.
    user.email = f"erased-{user.id}@privacy.invalid"
    user.display_name = None
    user.title = None
    user.department = None
    user.phone = None
    user.location = None
    user.timezone = None
    user.bio = None
    user.accent_color = None
    user.ui_prefs = None
    user.last_login_at = None
    user.last_seen_at = None
    clear_precise_location(user)
    user.is_active = False
    user.is_approved = False
    user.session_valid_after = current
    user.role = resolved_default_role
    user.granted_permissions = []
    user.access_version += 1
    if user.deactivated_at is None:
        user.deactivated_at = current
    user.erasure_requested_at = None
    user.erasure_due_at = None
    user.erased_at = current

    # Sessions are credentials/device identifiers and have no remaining product purpose. Keep the
    # immutable provider key and signed authority so the erased identity cannot reprovision or
    # become an "unclaimed" administrator bootstrap subject.
    db.execute(delete(UserSession).where(UserSession.user_id == user.id))
    # Contract versions and time remain for the configured legal-evidence period, but request
    # metadata no longer has a product purpose after erasure. Core is intentional: ordinary ORM
    # mutation/deletion is blocked by LegalAcceptance's append-only mapper guard.
    acceptance_table = cast(Table, LegalAcceptance.__table__)
    db.execute(
        acceptance_table.update()
        .where(
            acceptance_table.c.user_id == user.id,
            acceptance_table.c.personal_data_erased_at.is_(None),
        )
        .values(
            source_ip=None,
            user_agent=None,
            request_id=None,
            personal_data_erased_at=current,
        )
    )
    add_security_event(
        db,
        event_type="privacy.account.erased",
        outcome="success",
        target_type="user",
        target_id=user.id,
        metadata={"policy_trigger": policy_trigger},
    )


def prune_request_logs_governed(db: Session, limit: int, *, now: datetime | None = None) -> int:
    """Delete one bounded access-log batch after serializing with global legal hold."""
    if retention_hold_active(db):
        return 0
    cutoff = (now or _now()) - timedelta(days=settings.request_log_retention_days)
    stale_ids = list(
        db.scalars(
            select(RequestLog.id)
            .where(RequestLog.created_at <= cutoff)
            .order_by(RequestLog.created_at, RequestLog.id)
            .limit(limit)
        )
    )
    if stale_ids:
        db.execute(delete(RequestLog).where(RequestLog.id.in_(stale_ids)))
    return len(stale_ids)


def prune_sessions_governed(db: Session, limit: int, *, now: datetime | None = None) -> int:
    """Delete one bounded stale-session batch after serializing with legal hold."""
    if retention_hold_active(db):
        return 0
    return prune_stale_sessions(db, now=now, limit=limit)


def clear_stale_precise_locations(db: Session, limit: int, *, now: datetime | None = None) -> int:
    """Clear, rather than delete, one bounded batch of precise-location records."""
    if retention_hold_active(db):
        return 0
    cutoff = (now or _now()) - timedelta(hours=settings.precise_location_retention_hours)
    stale_ids = list(
        db.scalars(
            select(User.id)
            .where(
                or_(
                    User.last_location_at <= cutoff,
                    # Unknown capture time cannot justify retaining orphaned coordinates.
                    User.last_location_at.is_(None)
                    & or_(
                        User.last_latitude.is_not(None),
                        User.last_longitude.is_not(None),
                        User.last_location_accuracy.is_not(None),
                    ),
                ),
            )
            .order_by(User.last_location_at.is_not(None), User.last_location_at, User.id)
            .limit(limit)
            .with_for_update(skip_locked=True)
        )
    )
    if stale_ids:
        db.execute(
            update(User)
            .where(User.id.in_(stale_ids))
            .values(
                last_latitude=None,
                last_longitude=None,
                last_location_accuracy=None,
                last_location_at=None,
            )
        )
    return len(stale_ids)


def anonymize_due_accounts(db: Session, limit: int, *, now: datetime | None = None) -> int:
    """Anonymize due explicit, pending, and disabled non-admin accounts in one bounded batch."""
    if retention_hold_active(db):
        return 0
    current = now or _now()
    pending_cutoff = current - timedelta(days=settings.pending_account_retention_days)
    disabled_cutoff = current - timedelta(days=settings.disabled_account_retention_days)
    erasure_due = User.erasure_due_at
    deactivated_at = User.deactivated_at
    erased_at = User.erased_at
    explicit_due = erasure_due <= current
    pending_due = (User.is_approved.is_(False)) & (User.created_at <= pending_cutoff)
    disabled_due = (
        User.is_active.is_(False)
        & deactivated_at.is_not(None)
        & (deactivated_at <= disabled_cutoff)
    )
    priority = case((explicit_due, 0), (pending_due, 1), else_=2)
    users = list(
        db.scalars(
            select(User)
            .join(Role, User.role_id == Role.id)
            .where(
                erased_at.is_(None),
                Role.name != ADMIN_ROLE,
                or_(explicit_due, pending_due, disabled_due),
            )
            .options(
                selectinload(User.role),
                selectinload(User.granted_permissions),
            )
            .order_by(priority, User.id)
            .limit(limit)
            .with_for_update(skip_locked=True)
        )
    )
    default_role = db.scalar(select(Role).where(Role.name == DEFAULT_ROLE)) if users else None
    if users and default_role is None:
        raise RuntimeError("built-in user role is missing; account erasure refused")
    for user in users:
        due_at = user.erasure_due_at
        if due_at is not None and due_at <= current:
            trigger = "requested_erasure_due"
        elif not user.is_approved and user.created_at <= pending_cutoff:
            trigger = "pending_account_expired"
        else:
            trigger = "disabled_account_expired"
        anonymize_user(
            db,
            user,
            now=current,
            policy_trigger=trigger,
            default_role=default_role,
        )
    return len(users)


def prune_security_events_governed(db: Session, limit: int, *, now: datetime | None = None) -> int:
    """Purge expired facts in a bounded Core DELETE, including pending outbox rows by cascade."""
    if retention_hold_active(db):
        return 0
    current = now or _now()
    stale_ids = list(
        db.scalars(
            select(SecurityEvent.id)
            .where(SecurityEvent.retention_until <= current)
            .order_by(SecurityEvent.retention_until, SecurityEvent.id)
            .limit(limit)
            .with_for_update(skip_locked=True)
        )
    )
    if stale_ids:
        # Deliberately use the Core table rather than ORM instance deletion. Ordinary application
        # code remains subject to SecurityEvent's append-only mapper guard; this is the single
        # policy-governed expiry boundary.
        security_event_table = cast(Table, SecurityEvent.__table__)
        db.execute(security_event_table.delete().where(security_event_table.c.id.in_(stale_ids)))
    return len(stale_ids)


def prune_legal_acceptances_governed(
    db: Session, limit: int, *, now: datetime | None = None
) -> int:
    """Delete expired evidence under the earlier of cohort and current policy deadlines.

    ``retention_until`` preserves the deadline in force when the evidence was created. A later
    policy reduction is also applied to existing cohorts through the indexed ``accepted_at``
    cutoff. A policy increase can therefore never silently extend an already-stored deadline.
    """
    if retention_hold_active(db):
        return 0
    current = now or _now()
    stale_ids = list(
        db.scalars(
            select(LegalAcceptance.id)
            .where(LegalAcceptance.retention_until <= current)
            .order_by(LegalAcceptance.retention_until, LegalAcceptance.id)
            .limit(limit)
            .with_for_update(skip_locked=True)
        )
    )
    remaining = limit - len(stale_ids)
    if remaining > 0:
        policy_cutoff = current - timedelta(days=settings.legal_acceptance_retention_days)
        policy_query = (
            select(LegalAcceptance.id)
            .where(LegalAcceptance.accepted_at <= policy_cutoff)
            .order_by(LegalAcceptance.accepted_at, LegalAcceptance.id)
            .limit(remaining)
            .with_for_update(skip_locked=True)
        )
        if stale_ids:
            policy_query = policy_query.where(LegalAcceptance.id.not_in(stale_ids))
        stale_ids.extend(db.scalars(policy_query))
    if stale_ids:
        acceptance_table = cast(Table, LegalAcceptance.__table__)
        db.execute(acceptance_table.delete().where(acceptance_table.c.id.in_(stale_ids)))
    return len(stale_ids)
