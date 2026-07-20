"""Shared current locking reads for account lifecycle-sensitive mutations.

MySQL's default REPEATABLE READ can leave an ORM identity-map object stale after a retention
worker anonymizes the same account. Mutation paths use this helper immediately before changing
account/session data. Callers that also need the singleton site-policy lock must acquire policy
first, then the user lock, matching the retention worker's global lock order.
"""

from sqlalchemy import select
from sqlalchemy.orm import Session, attributes, lazyload

from app.models import Permission, Role, SiteSettings, User, role_permissions, user_permissions


def lock_current_user(db: Session, user_id: int) -> User | None:
    """Return the latest user row under ``FOR UPDATE``, replacing any cached stale state."""
    user = db.scalar(
        select(User)
        .where(User.id == user_id)
        # User access relationships default to select-in loading. Disable that implicit ordinary
        # SELECT here: under REPEATABLE READ it could repopulate an earlier authorization snapshot
        # immediately after the current locking scalar read.
        .options(lazyload(User.role), lazyload(User.granted_permissions))
        .with_for_update()
        .execution_options(populate_existing=True)
    )
    if user is not None:
        # ``populate_existing`` refreshes columns but intentionally leaves a relationship that an
        # earlier dependency already loaded. Force callers either to read it after their commit
        # (a new snapshot) or explicitly refresh it under locks with the helper below.
        db.expire(user, ["role", "granted_permissions"])
    return user


def lock_current_role(db: Session, role_id: int) -> Role | None:
    """Return the latest role and permission set under ``FOR UPDATE``.

    ``Role`` can already be present through ``actor.role``. ``populate_existing`` refreshes its
    columns, while explicitly expiring the collection prevents an earlier permission snapshot
    from surviving a check-and-act transaction.
    """
    role = db.scalar(
        select(Role)
        .where(Role.id == role_id)
        .with_for_update()
        .execution_options(populate_existing=True)
    )
    if role is not None:
        # Under MySQL REPEATABLE READ, an ordinary lazy relationship load can still reuse the
        # transaction's earlier consistent snapshot even after the parent row's locking read.
        # Make membership itself a current locking read, then install it as clean ORM state.
        permissions = list(
            db.scalars(
                select(Permission)
                .join(role_permissions, Permission.id == role_permissions.c.permission_id)
                .where(role_permissions.c.role_id == role.id)
                .order_by(Permission.key)
                .with_for_update()
                .execution_options(populate_existing=True)
            )
        )
        attributes.set_committed_value(role, "permissions", permissions)
    return role


def refresh_locked_user_access(db: Session, user: User) -> Role | None:
    """Install current role and direct-grant relationships for an already locked user row."""
    role = lock_current_role(db, user.role_id)
    if role is None:
        return None
    direct_permissions = list(
        db.scalars(
            select(Permission)
            .join(user_permissions, Permission.id == user_permissions.c.permission_id)
            .where(user_permissions.c.user_id == user.id)
            .order_by(Permission.key)
            .with_for_update()
            .execution_options(populate_existing=True)
        )
    )
    attributes.set_committed_value(user, "role", role)
    attributes.set_committed_value(user, "granted_permissions", direct_permissions)
    return role


def lock_current_site_settings(db: Session) -> SiteSettings | None:
    """Return the latest singleton policy row under ``FOR UPDATE``."""
    return db.scalar(
        select(SiteSettings)
        .where(SiteSettings.id == 1)
        .with_for_update()
        .execution_options(populate_existing=True)
    )


def account_is_authenticatable(user: User | None) -> bool:
    """Whether current lifecycle state permits authentication and personal-data mutation."""
    return bool(user is not None and user.erased_at is None and user.is_active and user.is_approved)
