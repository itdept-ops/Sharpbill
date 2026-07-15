import csv
import io
from datetime import UTC, datetime

from fastapi import APIRouter, Depends, Query, Response
from sqlalchemy import Select, func, or_, select
from sqlalchemy.exc import SQLAlchemyError
from sqlalchemy.orm import Session, selectinload

from app.auth.deps import get_current_user, require_permission
from app.auth.sessions import revoke_all_for_user, revoke_session
from app.db import get_db
from app.errors import ApiError
from app.models import Permission, Role, User, UserSession
from app.permissions import ADMIN_ROLE, PRESENCE_KICK, USERS_MANAGE, USERS_READ
from app.presence import is_online, online_cutoff
from app.schemas.auth import SessionOut
from app.schemas.user import (
    BulkActionRequest,
    PermissionGrantRequest,
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


def _assert_not_admin_target(actor: User, target: User) -> None:
    """A non-admin may not perform destructive actions on a full-admin account.

    Stops a mid-tier delegate (users.manage / presence.kick) from disabling, demoting, or
    force-logging-out administrators who outrank them. Admins may act on anyone (still subject
    to the self-modification and last-admin guards). Scoped to the top-level admin role rather
    than a full permission-subset comparison so ordinary member management (a users.manage
    delegate acting on plain users) is unaffected.
    """
    if not _is_admin(actor) and target.role_name == ADMIN_ROLE:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "Only an admin can deactivate, demote, or kick an administrator",
        )


def _active_admin_count(db: Session) -> int:
    """Count active, approved admins, locking those rows FOR UPDATE.

    The row lock turns every last-admin guard into a true check-and-act: concurrent
    demote/deactivate requests serialize on the admin rows instead of both reading a stale
    snapshot count and racing the active-admin population to zero (TOCTOU / write-skew).
    """
    rows = db.scalars(
        select(User.id)
        .join(Role, User.role_id == Role.id)
        .where(Role.name == ADMIN_ROLE, User.is_active.is_(True), User.is_approved.is_(True))
        .with_for_update()
    ).all()
    return len(rows)


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
    if online is not None:
        cutoff = online_cutoff()
        if online:
            stmt = stmt.where(User.last_seen_at.is_not(None), User.last_seen_at >= cutoff)
        else:  # online=false must mean "offline only", not "no filter"
            stmt = stmt.where(or_(User.last_seen_at.is_(None), User.last_seen_at < cutoff))
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
    # Eager-load identities here (the one multi-user serialization) so lazy loading doesn't N+1.
    users = list(
        db.scalars(stmt.options(selectinload(User.identities)).limit(limit).offset(offset))
    )
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
    current: User = Depends(require_permission(USERS_READ)),
    search: str | None = Query(None, max_length=100),
    role_id: int | None = Query(None),
    status: str | None = Query(None, pattern="^(active|pending|disabled)$"),
    online: bool | None = Query(None),
) -> Response:
    users = list(db.scalars(_filtered(search, role_id, status, online)))
    # Location can be GPS-derived, so only a manager exports it (matches the API gating).
    can_loc = USERS_MANAGE in current.permission_keys
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
                _csv_safe(u.location or "") if can_loc else "",
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
            _assert_not_admin_target(actor, user)
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
            if body.action == "deactivate":
                revoke_all_for_user(db, uid)  # sign the disabled user out of every device
            results.append({"id": uid, "ok": True})
        except ApiError as e:
            db.rollback()
            results.append({"id": uid, "ok": False, "error": e.detail["code"]})
        except SQLAlchemyError:
            # A commit-time DB error (deadlock, lock-wait, integrity) must not abort the whole
            # batch with a 500 and leave an unreported partial application — record and continue.
            db.rollback()
            results.append({"id": uid, "ok": False, "error": "DB_ERROR"})

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
    data = body.model_dump(exclude_unset=True)
    if "ui_prefs" in data:
        incoming = data.pop("ui_prefs")
        if incoming is None:
            user.ui_prefs = None  # explicit null resets every axis to defaults
        else:
            # Row-lock before the read-modify-write so concurrent single-key PATCHes serialize
            # instead of clobbering each other (JSON columns have no atomic in-place merge, and
            # a lost update would silently drop a just-changed setting). Reassign a fresh dict —
            # MySQL JSON has no in-place dirty tracking, so mutating the dict would not persist.
            db.refresh(user, with_for_update=True)
            user.ui_prefs = {**(user.ui_prefs or {}), **incoming}
    for field, value in data.items():
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
    _assert_not_admin_target(actor, user)
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


@router.put("/{user_id}/permissions", response_model=UserOut)
def set_user_permissions(
    user_id: int,
    body: PermissionGrantRequest,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> UserOut:
    """Replace the permissions granted DIRECTLY to a user (on top of their role)."""
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot change your own permissions")
    user = _get_target(db, user_id)

    keys = {k.strip().lower() for k in body.permission_keys if k.strip()}
    perms = list(db.scalars(select(Permission).where(Permission.key.in_(keys)))) if keys else []
    unknown = keys - {p.key for p in perms}
    if unknown:
        raise ApiError(
            400, "UNKNOWN_PERMISSION", f"Unknown permission(s): {', '.join(sorted(unknown))}"
        )
    # No privilege amplification: you can only grant permissions you already hold (admins hold all).
    if not _is_admin(actor) and not keys <= actor.permission_keys:
        raise ApiError(
            403, "INSUFFICIENT_PRIVILEGE", "You cannot grant a permission you do not hold"
        )

    user.granted_permissions = perms
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
    _assert_not_admin_target(actor, user)
    if not body.is_active:
        if user.role_name == ADMIN_ROLE and _active_admin_count(db) <= 1:
            raise ApiError(403, "LAST_ADMIN", "Cannot deactivate the last remaining admin")
        user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    user.is_active = body.is_active
    db.commit()
    if not body.is_active:
        # Mark the per-device session rows revoked too (the epoch already blocks token use, but
        # this keeps the sessions API from listing phantom "active" devices for a disabled user).
        revoke_all_for_user(db, user.id)
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
    _assert_not_admin_target(actor, user)
    user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    db.commit()
    revoke_all_for_user(db, user.id)  # sign the user out of every device
    # presence.kick is distinct from users.manage, so gate the target's GPS the same way the
    # directory does — a kicker without users.manage must not learn the target's coordinates.
    include_location = user.id == actor.id or USERS_MANAGE in actor.permission_keys
    return UserOut.from_user(
        user, online=is_online(user.last_seen_at), include_location=include_location
    )


@router.get("/{user_id}/sessions", response_model=list[SessionOut])
def list_user_sessions(
    user_id: int,
    db: Session = Depends(get_db),
    current: User = Depends(require_permission(USERS_READ)),
) -> list[SessionOut]:
    """A user's active sessions (one per signed-in device)."""
    _get_target(db, user_id)
    # Session IP is location-adjacent PII: only a manager (users.manage) or the user themselves
    # sees it — a users.read-only viewer gets it masked, matching the GPS-gating model.
    include_ip = user_id == current.id or USERS_MANAGE in current.permission_keys
    rows = db.scalars(
        select(UserSession)
        .where(UserSession.user_id == user_id, UserSession.revoked_at.is_(None))
        .order_by(UserSession.created_at.desc())
    )
    return [SessionOut.from_row(s, current=False, include_ip=include_ip) for s in rows]


@router.delete("/{user_id}/sessions/{session_id}", status_code=204)
def revoke_user_session(
    user_id: int,
    session_id: int,
    response: Response,
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(PRESENCE_KICK)),
) -> Response:
    """Sign out one of a user's devices."""
    session = db.get(UserSession, session_id)
    if session is None or session.user_id != user_id:
        raise ApiError(404, "NOT_FOUND", "Session not found")
    if session.revoked_at is None:
        revoke_session(session, db)
    response.status_code = 204
    return response
