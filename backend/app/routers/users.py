import csv
import io
from datetime import UTC, datetime

from fastapi import APIRouter, Depends, Query, Response
from sqlalchemy import Select, func, or_, select
from sqlalchemy.orm import Session

from app.auth.deps import get_current_user, require_permission
from app.db import get_db
from app.errors import ApiError
from app.models import Role, User
from app.permissions import ADMIN_ROLE, PRESENCE_KICK, USERS_MANAGE, USERS_READ
from app.presence import is_online, online_cutoff
from app.schemas.user import (
    BulkActionRequest,
    ProfileUpdate,
    RoleAssignRequest,
    StatusUpdateRequest,
    UserListOut,
    UserOut,
)
from app.sqlutil import escape_like

router = APIRouter()


def _csv_safe(value: str) -> str:
    # Neutralize spreadsheet formula injection (=, +, -, @, tab, CR at a cell's start).
    return "'" + value if value and value[0] in "=+-@\t\r" else value


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


def _filtered(
    search: str | None, role_id: int | None, status: str | None, online: bool | None
) -> Select:
    stmt = select(User)
    if search:
        like = f"%{escape_like(search.lower())}%"
        stmt = stmt.where(
            or_(
                func.lower(User.email).like(like, escape="\\"),
                func.lower(func.coalesce(User.display_name, "")).like(like, escape="\\"),
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
    return stmt.order_by(User.created_at.asc(), User.id.asc())


@router.get("", response_model=UserListOut)
def list_users(
    db: Session = Depends(get_db),
    current: User = Depends(require_permission(USERS_READ)),
    search: str | None = Query(None, max_length=100),
    role_id: int | None = Query(None),
    status: str | None = Query(None, pattern="^(active|pending|disabled)$"),
    online: bool | None = Query(None),
    limit: int = Query(100, ge=1, le=500),
    offset: int = Query(0, ge=0),
) -> UserListOut:
    stmt = _filtered(search, role_id, status, online)
    total = db.scalar(select(func.count()).select_from(stmt.subquery())) or 0
    users = list(db.scalars(stmt.limit(limit).offset(offset)))
    # Precise GPS is only shown to managers (users.manage) and to a user viewing themselves.
    can_loc = USERS_MANAGE in current.permission_keys
    return UserListOut(
        items=[
            UserOut.from_user(
                u,
                online=is_online(u.last_seen_at),
                include_location=can_loc or u.id == current.id,
            )
            for u in users
        ],
        total=total,
    )


@router.get("/export.csv")
def export_users_csv(
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(USERS_READ)),
    search: str | None = Query(None, max_length=100),
    role_id: int | None = Query(None),
    status: str | None = Query(None, pattern="^(active|pending|disabled)$"),
    online: bool | None = Query(None),
) -> Response:
    users = list(db.scalars(_filtered(search, role_id, status, online)))
    buf = io.StringIO()
    w = csv.writer(buf)
    w.writerow(
        [
            "id",
            "email",
            "display_name",
            "role",
            "status",
            "title",
            "department",
            "location",
            "created_at",
            "last_login_at",
        ]
    )
    for u in users:
        w.writerow(
            [
                u.id,
                _csv_safe(u.email),
                _csv_safe(u.display_name or ""),
                _csv_safe(u.role_name),
                _csv_safe(u.status),
                _csv_safe(u.title or ""),
                _csv_safe(u.department or ""),
                _csv_safe(u.location or ""),
                u.created_at.isoformat() if u.created_at else "",
                u.last_login_at.isoformat() if u.last_login_at else "",
            ]
        )
    return Response(
        content=buf.getvalue(),
        media_type="text/csv",
        headers={"Content-Disposition": "attachment; filename=users.csv"},
    )


@router.post("/bulk")
def bulk_action(
    body: BulkActionRequest,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> dict:
    """Apply an action to many users. Each id is committed independently; per-item errors are
    reported without aborting the batch. All the single-user guards apply."""
    role: Role | None = None
    if body.action == "assign_role":
        if body.role_id is None:
            raise ApiError(400, "UNKNOWN_ROLE", "role_id is required for assign_role")
        role = db.get(Role, body.role_id)
        if role is None:
            raise ApiError(400, "UNKNOWN_ROLE", "No such role")
        if not _is_admin(actor) and not role.permission_keys <= actor.permission_keys:
            raise ApiError(
                403,
                "INSUFFICIENT_PRIVILEGE",
                "You cannot assign a role with permissions you do not hold",
            )

    results = []
    for uid in body.ids:
        try:
            if uid == actor.id:
                raise ApiError(400, "CANNOT_MODIFY_SELF", "Cannot act on yourself")
            user = _get_target(db, uid)
            if body.action == "activate":
                user.is_active = True
            elif body.action == "deactivate":
                if user.role_name == ADMIN_ROLE and _active_admin_count(db) <= 1:
                    raise ApiError(403, "LAST_ADMIN", "Cannot deactivate the last remaining admin")
                user.is_active = False
                user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
            elif body.action == "approve":
                user.is_approved = True
            elif body.action == "assign_role":
                assert role is not None
                if (
                    user.role_name == ADMIN_ROLE
                    and role.name != ADMIN_ROLE
                    and _active_admin_count(db) <= 1
                ):
                    raise ApiError(403, "LAST_ADMIN", "Cannot demote the last remaining admin")
                user.role = role
            db.commit()
            results.append({"id": uid, "ok": True})
        except ApiError as e:
            db.rollback()
            results.append({"id": uid, "ok": False, "error": e.detail["code"]})

    return {"applied": sum(1 for r in results if r["ok"]), "results": results}


@router.get("/{user_id}", response_model=UserOut)
def get_user(
    user_id: int, db: Session = Depends(get_db), current: User = Depends(get_current_user)
) -> UserOut:
    if user_id != current.id and USERS_READ not in current.permission_keys:
        raise ApiError(403, "FORBIDDEN", "Missing permission: users.read")
    user = _get_target(db, user_id)
    include_location = user.id == current.id or USERS_MANAGE in current.permission_keys
    return UserOut.from_user(
        user, online=is_online(user.last_seen_at), include_location=include_location
    )


@router.patch("/{user_id}/profile", response_model=UserOut)
def update_profile(
    user_id: int,
    body: ProfileUpdate,
    db: Session = Depends(get_db),
    current: User = Depends(get_current_user),
) -> UserOut:
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
    if not _is_admin(actor) and not role.permission_keys <= actor.permission_keys:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "You cannot assign a role with permissions you do not hold",
        )
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
        user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    user.is_active = body.is_active
    db.commit()
    return UserOut.from_user(user, online=is_online(user.last_seen_at))


@router.post("/{user_id}/approve", response_model=UserOut)
def approve_user(
    user_id: int, db: Session = Depends(get_db), _: User = Depends(require_permission(USERS_MANAGE))
) -> UserOut:
    user = _get_target(db, user_id)
    user.is_approved = True
    db.commit()
    return UserOut.from_user(user, online=is_online(user.last_seen_at))


@router.post("/{user_id}/kick", response_model=UserOut)
def kick_user(
    user_id: int,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(PRESENCE_KICK)),
) -> UserOut:
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot kick your own session")
    user = _get_target(db, user_id)
    user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    db.commit()
    # presence.kick is distinct from users.manage, so gate the target's GPS the same way the
    # directory does — a kicker without users.manage must not learn the target's coordinates.
    include_location = user.id == actor.id or USERS_MANAGE in actor.permission_keys
    return UserOut.from_user(
        user, online=is_online(user.last_seen_at), include_location=include_location
    )
