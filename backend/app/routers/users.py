from datetime import UTC, datetime

from fastapi import APIRouter, Depends, Query
from sqlalchemy import func, or_, select
from sqlalchemy.orm import Session

from app.auth.deps import get_current_user, require_permission
from app.db import get_db
from app.errors import ApiError
from app.models import Role, User
from app.permissions import ADMIN_ROLE, PRESENCE_KICK, USERS_MANAGE, USERS_READ
from app.presence import is_online, online_cutoff
from app.schemas.user import (
    ProfileUpdate,
    RoleAssignRequest,
    StatusUpdateRequest,
    UserListOut,
    UserOut,
)

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
            .where(Role.name == ADMIN_ROLE, User.is_active.is_(True), User.is_approved.is_(True))
        )
        or 0
    )


@router.get("", response_model=UserListOut)
def list_users(
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(USERS_READ)),
    search: str | None = Query(None),
    role_id: int | None = Query(None),
    status: str | None = Query(None, pattern="^(active|pending|disabled)$"),
    online: bool | None = Query(None),
) -> UserListOut:
    stmt = select(User)
    if search:
        like = f"%{search.lower()}%"
        stmt = stmt.where(
            or_(
                func.lower(User.email).like(like),
                func.lower(func.coalesce(User.display_name, "")).like(like),
            )
        )
    if role_id is not None:
        stmt = stmt.where(User.role_id == role_id)
    if status == "active":
        stmt = stmt.where(User.is_active.is_(True), User.is_approved.is_(True))
    elif status == "pending":
        stmt = stmt.where(User.is_approved.is_(False))
    elif status == "disabled":
        stmt = stmt.where(User.is_active.is_(False), User.is_approved.is_(True))
    if online:
        stmt = stmt.where(User.last_seen_at.is_not(None), User.last_seen_at >= online_cutoff())

    users = list(db.scalars(stmt.order_by(User.created_at.asc(), User.id.asc())))
    return UserListOut(
        items=[UserOut.from_user(u, online=is_online(u.last_seen_at)) for u in users],
        total=len(users),
    )


@router.get("/{user_id}", response_model=UserOut)
def get_user(
    user_id: int, db: Session = Depends(get_db), current: User = Depends(get_current_user)
) -> UserOut:
    # Anyone may view their own record; viewing others needs users.read.
    if user_id != current.id and USERS_READ not in current.permission_keys:
        raise ApiError(403, "FORBIDDEN", "Missing permission: users.read")
    user = _get_target(db, user_id)
    return UserOut.from_user(user, online=is_online(user.last_seen_at))


@router.patch("/{user_id}/profile", response_model=UserOut)
def update_profile(
    user_id: int,
    body: ProfileUpdate,
    db: Session = Depends(get_db),
    current: User = Depends(get_current_user),
) -> UserOut:
    # You may edit your own profile; editing someone else's needs users.manage.
    if user_id != current.id and USERS_MANAGE not in current.permission_keys:
        raise ApiError(403, "FORBIDDEN", "You can only edit your own profile")
    user = _get_target(db, user_id)
    for field, value in body.model_dump(exclude_unset=True).items():
        setattr(user, field, value)
    db.commit()
    return UserOut.from_user(user, online=is_online(user.last_seen_at))


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
    # Amplification guard: you cannot hand out a role more powerful than your own.
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


@router.post("/{user_id}/approve", response_model=UserOut)
def approve_user(
    user_id: int,
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(USERS_MANAGE)),
) -> UserOut:
    """Approve a pending sign-up so the account can log in."""
    user = _get_target(db, user_id)
    user.is_approved = True
    db.commit()
    return UserOut.from_user(user, online=is_online(user.last_seen_at))


@router.post("/{user_id}/kick", response_model=UserOut)
def kick_user(
    user_id: int,
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(PRESENCE_KICK)),
) -> UserOut:
    """Force sign-out: invalidate every session token this user currently holds."""
    user = _get_target(db, user_id)
    user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    db.commit()
    return UserOut.from_user(user, online=is_online(user.last_seen_at))
