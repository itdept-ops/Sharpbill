import csv
import io
from datetime import UTC, datetime
from typing import NotRequired, TypedDict

from fastapi import APIRouter, Depends, Query, Request, Response
from sqlalchemy import Select, func, or_, select
from sqlalchemy.exc import SQLAlchemyError
from sqlalchemy.orm import Session, selectinload

from app.admin_access import administration_available
from app.auth.deps import get_current_user, require_permission
from app.auth.sessions import revoke_all_for_user, revoke_session
from app.concurrency import require_version
from app.config import settings
from app.db import get_db
from app.errors import ApiError
from app.models import Permission, Role, SiteSettings, User, UserSession
from app.permissions import (
    ADMIN_ROLE,
    PRESENCE_KICK,
    PRESENCE_VIEW,
    ROLES_MANAGE,
    USERS_EXPORT,
    USERS_MANAGE,
    USERS_READ,
)
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
from app.security_events import add_security_event, summarize_string_set
from app.sqlutil import escape_like

router = APIRouter()
_MAX_USER_EXPORT_ROWS = 10_000


class _BulkItem(TypedDict):
    id: int
    ok: bool
    error: NotRequired[str]


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


def _assert_can_manage_target(actor: User, target: User) -> None:
    """Prevent a delegated manager from mutating a principal who outranks them.

    Admins may act on anyone (subject to self/last-admin guards). Other delegates can manage a
    peer or subordinate only when every security-sensitive target permission is also held by the
    actor. Baseline directory/presence read grants do not establish management seniority; unknown
    custom grants do. This covers role and direct grants, not merely the literal admin role.
    """
    unheld_sensitive = (target.permission_keys - actor.permission_keys) - {
        USERS_READ,
        PRESENCE_VIEW,
    }
    outranks_actor = target.role_name == ADMIN_ROLE or bool(unheld_sensitive)
    if not _is_admin(actor) and outranks_actor:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "You cannot modify or revoke access for a principal who outranks you",
        )


def _assert_role_assignable(actor: User, role: Role) -> None:
    """Keep the special admin meta-role behind an explicit administrator boundary."""
    _assert_access_assignment_authority(actor)
    if role.name == ADMIN_ROLE and not _is_admin(actor):
        raise ApiError(403, "INSUFFICIENT_PRIVILEGE", "Only an admin can assign the admin role")
    if not _is_admin(actor) and not role.permission_keys <= actor.permission_keys:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "You cannot assign a role with permissions you do not hold",
        )


def _assert_access_assignment_authority(actor: User) -> None:
    """Role assignment/direct grants require both user and RBAC administration authority."""
    if ROLES_MANAGE not in actor.permission_keys:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "Changing access requires both users.manage and roles.manage",
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


def _lock_administration_boundary(db: Session) -> SiteSettings:
    site = db.scalar(select(SiteSettings).where(SiteSettings.id == 1).with_for_update())
    if site is None:
        raise ApiError(500, "SETTINGS_NOT_INITIALIZED", "Site settings are not initialized")
    return site


def _assert_administration_remains_available(db: Session, site: SiteSettings) -> None:
    db.flush()
    google = settings.google_provider_configured and bool(site.allow_google)
    microsoft = settings.microsoft_provider_configured and bool(site.allow_microsoft)
    if not administration_available(
        db,
        google=google,
        microsoft=microsoft,
        dev=settings.is_dev_auth_enabled,
        lock=True,
    ):
        raise ApiError(
            409,
            "ADMIN_ACCESS_STRANDED",
            "This change would leave no reachable administrator or bootstrap path",
        )


def _bulk_role(body: BulkActionRequest, db: Session, actor: User) -> Role | None:
    if body.action != "assign_role":
        return None
    if body.role_id is None:
        raise ApiError(400, "UNKNOWN_ROLE", "role_id is required for assign_role")
    role = db.get(Role, body.role_id)
    if role is None:
        raise ApiError(400, "UNKNOWN_ROLE", "No such role")
    _assert_role_assignable(actor, role)
    return role


def _mutate_bulk_user(db: Session, user: User, action: str, role: Role | None) -> None:
    if action == "activate":
        user.is_active = True
        return
    if action == "deactivate":
        if user.role_name == ADMIN_ROLE and _active_admin_count(db) <= 1:
            raise ApiError(403, "LAST_ADMIN", "Cannot deactivate the last remaining admin")
        user.is_active = False
        user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
        return
    if action == "approve":
        user.is_approved = True
        return
    assert role is not None
    if user.role_name == ADMIN_ROLE and role.name != ADMIN_ROLE and _active_admin_count(db) <= 1:
        raise ApiError(403, "LAST_ADMIN", "Cannot demote the last remaining admin")
    user.role = role
    user.access_version += 1


def _apply_bulk_user(
    db: Session,
    actor: User,
    request: Request,
    user_id: int,
    action: str,
    role: Role | None,
) -> None:
    """Apply one validated bulk item; the caller owns error mapping and transaction recovery."""
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "Cannot act on yourself")
    site = _lock_administration_boundary(db)
    if action == "assign_role":
        assert role is not None
        role = db.scalar(select(Role).where(Role.id == role.id).with_for_update())
        if role is None:
            raise ApiError(400, "UNKNOWN_ROLE", "No such role")
        _assert_role_assignable(actor, role)
    user = db.scalar(select(User).where(User.id == user_id).with_for_update())
    if user is None:
        raise ApiError(404, "NOT_FOUND", "User not found")
    _assert_can_manage_target(actor, user)

    before = {
        "is_active": bool(user.is_active),
        "is_approved": bool(user.is_approved),
        "role_id": user.role_id,
        "access_version": user.access_version,
    }
    _mutate_bulk_user(db, user, action, role)
    _assert_administration_remains_available(db, site)

    if action == "deactivate":
        revoke_all_for_user(db, user_id, commit=False)
    event_type = {
        "activate": "user.status.changed",
        "deactivate": "user.status.changed",
        "approve": "user.approved",
        "assign_role": "user.role.changed",
    }[action]
    metadata: dict = {
        "bulk": True,
        "action": action,
        "before": before,
        "after": {
            "is_active": bool(user.is_active),
            "is_approved": bool(user.is_approved),
            "role_id": user.role_id,
            "access_version": user.access_version,
        },
    }
    add_security_event(
        db,
        event_type=event_type,
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="user",
        target_id=user.id,
        metadata=metadata,
    )
    db.commit()


def _filtered(
    search: str | None, role_id: int | None, status: str | None, online: bool | None
) -> Select:
    stmt = select(User)
    if search:
        # The database's utf8mb4_0900_ai_ci collation is already case-insensitive; wrapping
        # indexed columns in LOWER() adds work without changing semantics. Substring matching is
        # intentionally retained for the directory UX and bounded by input/offset/read timeouts.
        like = f"%{escape_like(search)}%"
        stmt = stmt.where(
            or_(
                User.email.like(like, escape="\\"),
                func.coalesce(User.display_name, "").like(like, escape="\\"),
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
            stmt = stmt.where(
                User.is_active.is_(True),
                User.is_approved.is_(True),
                User.last_seen_at.is_not(None),
                User.last_seen_at >= cutoff,
            )
        else:  # online=false must mean "offline only", not "no filter"
            stmt = stmt.where(or_(User.last_seen_at.is_(None), User.last_seen_at < cutoff))
    return stmt.order_by(User.created_at.asc(), User.id.asc())


@router.get("", response_model=UserListOut)
def list_users(
    db: Session = Depends(get_db),
    current: User = Depends(require_permission(USERS_READ)),
    search: str | None = Query(None, min_length=2, max_length=100),
    role_id: int | None = Query(None),
    status: str | None = Query(None, pattern="^(active|pending|disabled)$"),
    online: bool | None = Query(None),
    limit: int = Query(100, ge=1, le=500),
    offset: int = Query(0, ge=0, le=10_000),
) -> UserListOut:
    stmt = _filtered(search, role_id, status, online)
    total = db.scalar(select(func.count()).select_from(stmt.subquery())) or 0
    # Eager-load identities here (the one multi-user serialization) so lazy loading doesn't N+1.
    users = list(
        db.scalars(stmt.options(selectinload(User.identities)).limit(limit).offset(offset))
    )
    # Precise GPS is only shown to managers (users.manage) and to a user viewing themselves.
    can_loc = USERS_MANAGE in current.permission_keys
    can_view_subjects = _is_admin(current)
    return UserListOut(
        items=[
            UserOut.from_user(
                u,
                online=is_online(u.last_seen_at),
                include_location=can_loc or u.id == current.id,
                include_identity_subjects=can_view_subjects or u.id == current.id,
            )
            for u in users
        ],
        total=total,
    )


@router.get("/export.csv")
def export_users_csv(
    request: Request,
    db: Session = Depends(get_db),
    current: User = Depends(require_permission(USERS_EXPORT)),
    search: str | None = Query(None, min_length=2, max_length=100),
    role_id: int | None = Query(None),
    status: str | None = Query(None, pattern="^(active|pending|disabled)$"),
    online: bool | None = Query(None),
) -> Response:
    # Location can be GPS-derived, so only a manager exports it (matches the API gating).
    can_loc = USERS_MANAGE in current.permission_keys
    header = [
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

    # Materialize a hard-bounded result while the request owns its DB session. Returning a
    # generator would keep the transaction/session open for as long as a slow client downloads.
    users = list(
        db.scalars(_filtered(search, role_id, status, online).limit(_MAX_USER_EXPORT_ROWS + 1))
    )
    if len(users) > _MAX_USER_EXPORT_ROWS:
        raise ApiError(
            413,
            "EXPORT_TOO_LARGE",
            f"The export exceeds {_MAX_USER_EXPORT_ROWS:,} rows; narrow the filters and retry",
        )
    buffer = io.StringIO()
    writer = csv.writer(buffer)
    writer.writerow(header)
    for user in users:
        writer.writerow(
            [
                user.id,
                _csv_safe(user.email),
                _csv_safe(user.display_name or ""),
                _csv_safe(user.role_name),
                _csv_safe(user.status),
                _csv_safe(user.title or ""),
                _csv_safe(user.department or ""),
                _csv_safe(user.location or "") if can_loc else "",
                user.created_at.isoformat() if user.created_at else "",
                user.last_login_at.isoformat() if user.last_login_at else "",
            ]
        )
    add_security_event(
        db,
        event_type="users.exported",
        outcome="success",
        request=request,
        actor_user_id=current.id,
        target_type="user_collection",
        metadata={
            "exported_count": len(users),
            "filters_applied": bool(search or role_id is not None or status or online is not None),
        },
    )
    db.commit()
    return Response(
        content=buffer.getvalue(),
        media_type="text/csv",
        headers={"Content-Disposition": "attachment; filename=users.csv"},
    )


@router.post("/bulk")
def bulk_action(
    body: BulkActionRequest,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> dict:
    """Apply an action to many users. Each id is committed independently; per-item errors are
    reported without aborting the batch. All the single-user guards apply."""
    role = _bulk_role(body, db, actor)

    results: list[_BulkItem] = []
    for uid in body.ids:
        try:
            _apply_bulk_user(db, actor, request, uid, body.action, role)
            results.append({"id": uid, "ok": True})
        except ApiError as e:
            db.rollback()
            results.append({"id": uid, "ok": False, "error": e.code})
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
        user,
        online=is_online(user.last_seen_at),
        include_location=include_location,
        include_identity_subjects=user.id == current.id or _is_admin(current),
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
    if user_id != current.id:
        _assert_can_manage_target(current, user)
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
    return UserOut.from_user(
        user,
        online=is_online(user.last_seen_at),
        include_identity_subjects=user.id == current.id or _is_admin(current),
    )


@router.patch("/{user_id}/role", response_model=UserOut)
def update_role(
    user_id: int,
    body: RoleAssignRequest,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> UserOut:
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot change your own role")
    site = _lock_administration_boundary(db)
    role = db.scalar(select(Role).where(Role.id == body.role_id).with_for_update())
    if role is None:
        raise ApiError(400, "UNKNOWN_ROLE", "No such role")
    _assert_role_assignable(actor, role)
    user = db.scalar(select(User).where(User.id == user_id).with_for_update())
    if user is None:
        raise ApiError(404, "NOT_FOUND", "User not found")
    _assert_can_manage_target(actor, user)
    require_version(body.expected_version, user.access_version, "User access")
    previous_role_id = user.role_id
    previous_role_name = user.role_name
    previous_version = user.access_version
    if user.role_name == ADMIN_ROLE and role.name != ADMIN_ROLE and _active_admin_count(db) <= 1:
        raise ApiError(403, "LAST_ADMIN", "Cannot demote the last remaining admin")
    user.role = role
    user.access_version += 1
    _assert_administration_remains_available(db, site)
    add_security_event(
        db,
        event_type="user.role.changed",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="user",
        target_id=user.id,
        metadata={
            "before": {
                "role_id": previous_role_id,
                "role_name": previous_role_name,
                "access_version": previous_version,
            },
            "after": {
                "role_id": role.id,
                "role_name": role.name,
                "access_version": user.access_version,
            },
        },
    )
    db.commit()
    return UserOut.from_user(
        user, online=is_online(user.last_seen_at), include_identity_subjects=_is_admin(actor)
    )


@router.put("/{user_id}/permissions", response_model=UserOut)
def set_user_permissions(
    user_id: int,
    body: PermissionGrantRequest,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> UserOut:
    """Replace the permissions granted DIRECTLY to a user (on top of their role)."""
    _assert_access_assignment_authority(actor)
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot change your own permissions")
    user = db.scalar(select(User).where(User.id == user_id).with_for_update())
    if user is None:
        raise ApiError(404, "NOT_FOUND", "User not found")
    _assert_can_manage_target(actor, user)
    require_version(body.expected_version, user.access_version, "User access")
    previous_keys = set(user.direct_permission_keys)
    previous_version = user.access_version

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
    user.access_version += 1
    add_security_event(
        db,
        event_type="user.permissions.changed",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="user",
        target_id=user.id,
        metadata={
            "before": {
                "permissions": summarize_string_set(previous_keys),
                "access_version": previous_version,
            },
            "after": {
                "permissions": summarize_string_set(keys),
                "access_version": user.access_version,
            },
        },
    )
    db.commit()
    return UserOut.from_user(
        user, online=is_online(user.last_seen_at), include_identity_subjects=_is_admin(actor)
    )


@router.patch("/{user_id}/status", response_model=UserOut)
def update_status(
    user_id: int,
    body: StatusUpdateRequest,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> UserOut:
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot deactivate yourself")
    site = _lock_administration_boundary(db)
    user = db.scalar(select(User).where(User.id == user_id).with_for_update())
    if user is None:
        raise ApiError(404, "NOT_FOUND", "User not found")
    _assert_can_manage_target(actor, user)
    previous_active = bool(user.is_active)
    if not body.is_active:
        if user.role_name == ADMIN_ROLE and _active_admin_count(db) <= 1:
            raise ApiError(403, "LAST_ADMIN", "Cannot deactivate the last remaining admin")
        user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    user.is_active = body.is_active
    if not body.is_active:
        # Mark the per-device session rows revoked too (the epoch already blocks token use, but
        # this keeps the sessions API from listing phantom "active" devices for a disabled user).
        revoke_all_for_user(db, user.id, commit=False)
        _assert_administration_remains_available(db, site)
    add_security_event(
        db,
        event_type="user.status.changed",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="user",
        target_id=user.id,
        metadata={
            "before": {"is_active": previous_active},
            "after": {"is_active": body.is_active},
        },
    )
    db.commit()
    return UserOut.from_user(
        user, online=is_online(user.last_seen_at), include_identity_subjects=_is_admin(actor)
    )


@router.post("/{user_id}/approve", response_model=UserOut)
def approve_user(
    user_id: int,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(USERS_MANAGE)),
) -> UserOut:
    user = _get_target(db, user_id)
    _assert_can_manage_target(actor, user)
    previous_approved = bool(user.is_approved)
    user.is_approved = True
    add_security_event(
        db,
        event_type="user.approved",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="user",
        target_id=user.id,
        metadata={
            "before": {"is_approved": previous_approved},
            "after": {"is_approved": True},
        },
    )
    db.commit()
    return UserOut.from_user(
        user, online=is_online(user.last_seen_at), include_identity_subjects=_is_admin(actor)
    )


@router.post("/{user_id}/kick", response_model=UserOut)
def kick_user(
    user_id: int,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(PRESENCE_KICK)),
) -> UserOut:
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "You cannot kick your own session")
    user = _get_target(db, user_id)
    _assert_can_manage_target(actor, user)
    user.session_valid_after = datetime.now(UTC).replace(tzinfo=None)
    revoke_all_for_user(db, user.id, commit=False)  # sign the user out of every device
    add_security_event(
        db,
        event_type="user.sessions.revoked",
        outcome="success",
        severity="warning",
        request=request,
        actor_user_id=actor.id,
        target_type="user",
        target_id=user.id,
        metadata={"scope": "all"},
    )
    db.commit()
    # presence.kick is distinct from users.manage, so gate the target's GPS the same way the
    # directory does — a kicker without users.manage must not learn the target's coordinates.
    include_location = user.id == actor.id or USERS_MANAGE in actor.permission_keys
    return UserOut.from_user(
        user,
        online=is_online(user.last_seen_at),
        include_location=include_location,
        include_identity_subjects=_is_admin(actor),
    )


@router.get("/{user_id}/sessions", response_model=list[SessionOut])
def list_user_sessions(
    user_id: int,
    db: Session = Depends(get_db),
    current: User = Depends(require_permission(USERS_READ)),
) -> list[SessionOut]:
    """A user's active sessions (one per signed-in device)."""
    _get_target(db, user_id)
    # IP and user-agent together identify a device. Only a manager (users.manage) or the user
    # themselves sees either; a directory-only viewer gets both values masked.
    include_device_details = user_id == current.id or USERS_MANAGE in current.permission_keys
    rows = db.scalars(
        select(UserSession)
        .where(
            UserSession.user_id == user_id,
            UserSession.revoked_at.is_(None),
            UserSession.expires_at > datetime.now(UTC).replace(tzinfo=None),
        )
        .order_by(UserSession.created_at.desc())
    )
    return [
        SessionOut.from_row(s, current=False, include_device_details=include_device_details)
        for s in rows
    ]


@router.delete("/{user_id}/sessions/{session_id}", status_code=204)
def revoke_user_session(
    user_id: int,
    session_id: int,
    request: Request,
    response: Response,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(PRESENCE_KICK)),
) -> Response:
    """Sign out one of a user's devices."""
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "Use the personal sessions endpoint")
    user = _get_target(db, user_id)
    _assert_can_manage_target(actor, user)
    session = db.get(UserSession, session_id)
    if session is None or session.user_id != user_id:
        raise ApiError(404, "NOT_FOUND", "Session not found")
    if session.revoked_at is None:
        revoke_session(session, db, commit=False)
    add_security_event(
        db,
        event_type="session.revoked",
        outcome="success",
        severity="warning",
        request=request,
        actor_user_id=actor.id,
        target_type="user",
        target_id=user.id,
        metadata={"scope": "single", "session_id": session.id},
    )
    db.commit()
    response.status_code = 204
    return response
