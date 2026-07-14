from datetime import UTC, datetime

from fastapi import APIRouter, Depends
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.auth.deps import require_permission
from app.db import get_db
from app.errors import ApiError
from app.models import Role, User
from app.permissions import ADMIN_ROLE, PRESENCE_KICK, USERS_MANAGE, USERS_READ
from app.presence import is_online
from app.schemas.user import RoleAssignRequest, StatusUpdateRequest, UserListOut, UserOut

router = APIRouter()


def _get_target(db: Session, user_id: int) -> User:
    user = db.get(User, user_id)
    if user is None:
        raise ApiError(404, "NOT_FOUND", "User not found")
    return user


def _is_admin(user: User) -> bool:
    return user.role_name == ADMIN_ROLE


def _active_admin_count(db: Session) -> int:
    return (
        db.scalar(
            select(func.count())
            .select_from(User)
            .join(Role, User.role_id == Role.id)
            .where(Role.name == ADMIN_ROLE, User.is_active.is_(True))
        )
        or 0
    )


@router.get("", response_model=UserListOut)
def list_users(
    db: Session = Depends(get_db), _: User = Depends(require_permission(USERS_READ))
) -> UserListOut:
    users = list(db.scalars(select(User).order_by(User.created_at.asc(), User.id.asc())))
    total = db.scalar(select(func.count()).select_from(User)) or 0
    return UserListOut(
        items=[UserOut.from_user(u, online=is_online(u.last_seen_at)) for u in users],
        total=total,
    )


@router.patch("/{user_id}/role", response_model=UserOut)
def update_role(
    user_id: int,
    body: RoleAssignRequest,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> UserOut:
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot change your own role")
    user = _get_target(db, user_id)
    role = db.get(Role, body.role_id)
    if role is None:
        raise ApiError(400, "UNKNOWN_ROLE", "No such role")
    # Amplification guard: you cannot hand out a role more powerful than your own (this blocks
    # a users.manage delegate from making a puppet account a full admin).
    if not _is_admin(actor) and not role.permission_keys <= actor.permission_keys:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "You cannot assign a role with permissions you do not hold",
        )
    # Last-admin guard: never demote away the final active admin.
    if user.role_name == ADMIN_ROLE and role.name != ADMIN_ROLE and _active_admin_count(db) <= 1:
        raise ApiError(403, "LAST_ADMIN", "Cannot demote the last remaining admin")
    user.role = role
    db.commit()
    return UserOut.from_user(user, online=is_online(user.last_seen_at))


@router.patch("/{user_id}/status", response_model=UserOut)
def update_status(
    user_id: int,
    body: StatusUpdateRequest,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> UserOut:
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot deactivate yourself")
    user = _get_target(db, user_id)
    if not body.is_active:
        if user.role_name == ADMIN_ROLE and _active_admin_count(db) <= 1:
            raise ApiError(403, "LAST_ADMIN", "Cannot deactivate the last remaining admin")
        # Durable revocation: kill existing sessions so reactivation can't resurrect old tokens.
        user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    user.is_active = body.is_active
    db.commit()
    return UserOut.from_user(user, online=is_online(user.last_seen_at))


@router.post("/{user_id}/kick", response_model=UserOut)
def kick_user(
    user_id: int,
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(PRESENCE_KICK)),
) -> UserOut:
    """Force sign-out: invalidate every session token this user currently holds.

    Their existing cookie is rejected on the next request (401 SESSION_REVOKED); they can
    sign in again to get a fresh session. Unlike deactivation, the account stays enabled.
    """
    user = _get_target(db, user_id)
    user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    db.commit()
    return UserOut.from_user(user, online=is_online(user.last_seen_at))
