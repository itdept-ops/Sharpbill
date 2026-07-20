from fastapi import APIRouter, Depends, Query, Request, Response
from sqlalchemy import func, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session, selectinload

from app.auth.deps import require_permission
from app.concurrency import require_version
from app.db import get_db
from app.errors import ApiError
from app.models import Permission, Role, SiteSettings, User
from app.permissions import ADMIN_ROLE, ROLES_MANAGE
from app.schemas.role import (
    PermissionCreate,
    PermissionOut,
    RoleCreate,
    RoleOut,
    RoleUpdate,
)
from app.security_events import add_security_event, summarize_string_set

router = APIRouter()


def _is_admin(user: User) -> bool:
    return user.role_name == ADMIN_ROLE


def _role_out(db: Session, role: Role, *, user_count: int | None = None) -> RoleOut:
    # Single-role mutation responses still need an exact count. Collection callers pass the
    # aggregate value so listing N roles never issues N additional COUNT queries.
    count = (
        db.scalar(select(func.count()).select_from(User).where(User.role_id == role.id)) or 0
        if user_count is None
        else user_count
    )
    return RoleOut(
        id=role.id,
        name=role.name,
        description=role.description,
        is_system=role.is_system,
        permissions=[
            PermissionOut.model_validate(p, from_attributes=True) for p in role.permissions
        ],
        user_count=count,
        version=role.version,
    )


def _normalize(keys: list[str]) -> list[str]:
    seen: dict[str, None] = {}
    for k in keys:
        k = k.strip().lower()
        if k:
            seen.setdefault(k, None)
    return list(seen.keys())


def _resolve_permissions(db: Session, keys: list[str]) -> list[Permission]:
    keys = _normalize(keys)
    if not keys:
        return []
    perms = list(db.scalars(select(Permission).where(Permission.key.in_(keys))))
    missing = set(keys) - {p.key for p in perms}
    if missing:
        raise ApiError(
            400, "UNKNOWN_PERMISSION", "Unknown permissions: " + ", ".join(sorted(missing))
        )
    return perms


def _guard_grantable(actor: User, keys: list[str]) -> None:
    """Privilege-amplification guard: you may only attach permissions you yourself hold.

    A full admin may attach anything — including a just-created custom permission that is not
    yet on any role (and so is in nobody's effective set). Without this bypass an admin could
    create a permission but never wire it to a role. For non-admins it stops a delegate with
    roles.manage from minting a role that carries permissions they lack and climbing.
    """
    if _is_admin(actor):
        return
    extra = set(_normalize(keys)) - actor.permission_keys
    if extra:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "You can only grant permissions you hold; missing: " + ", ".join(sorted(extra)),
        )


def _changed_role_fields(
    role: Role,
    *,
    previous_name: str,
    previous_description: str | None,
    previous_permissions: list[str],
) -> list[str]:
    changed = []
    if role.name != previous_name:
        changed.append("name")
    if role.description != previous_description:
        changed.append("description")
    if sorted(role.permission_keys) != previous_permissions:
        changed.append("permission_keys")
    return changed


def _lock_site_settings(db: Session) -> SiteSettings:
    """Use the singleton as the global lock-order root for settings/role mutations."""
    site = db.scalar(select(SiteSettings).where(SiteSettings.id == 1).with_for_update())
    if site is None:
        raise ApiError(500, "SETTINGS_NOT_INITIALIZED", "Site settings are not initialized")
    return site


# ---- permissions ----
@router.get("/permissions", response_model=list[PermissionOut])
def list_permissions(
    db: Session = Depends(get_db), _: User = Depends(require_permission(ROLES_MANAGE))
) -> list[PermissionOut]:
    perms = db.scalars(select(Permission).order_by(Permission.key)).all()
    return [PermissionOut.model_validate(p, from_attributes=True) for p in perms]


@router.post("/permissions", response_model=PermissionOut, status_code=201)
def create_permission(
    body: PermissionCreate,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(ROLES_MANAGE)),
) -> PermissionOut:
    if db.scalar(select(Permission).where(Permission.key == body.key)):
        raise ApiError(409, "ALREADY_EXISTS", f"Permission '{body.key}' already exists")
    perm = Permission(key=body.key, description=body.description, is_system=False)
    db.add(perm)
    try:
        db.flush()
        add_security_event(
            db,
            event_type="rbac.permission.created",
            outcome="success",
            request=request,
            actor_user_id=actor.id,
            target_type="permission",
            target_id=perm.id,
            metadata={"permission_key": perm.key},
        )
        db.commit()
    except IntegrityError:  # concurrent create raced us
        db.rollback()
        raise ApiError(409, "ALREADY_EXISTS", f"Permission '{body.key}' already exists") from None
    db.refresh(perm)
    return PermissionOut.model_validate(perm, from_attributes=True)


# ---- roles ----
@router.get("/roles", response_model=list[RoleOut])
def list_roles(
    db: Session = Depends(get_db), _: User = Depends(require_permission(ROLES_MANAGE))
) -> list[RoleOut]:
    counts = (
        select(User.role_id.label("role_id"), func.count(User.id).label("user_count"))
        .group_by(User.role_id)
        .subquery()
    )
    rows = db.execute(
        select(Role, func.coalesce(counts.c.user_count, 0))
        .options(selectinload(Role.permissions))
        .outerjoin(counts, counts.c.role_id == Role.id)
        .order_by(Role.id)
    ).all()
    return [_role_out(db, role, user_count=int(user_count)) for role, user_count in rows]


@router.post("/roles", response_model=RoleOut, status_code=201)
def create_role(
    body: RoleCreate,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(ROLES_MANAGE)),
) -> RoleOut:
    if db.scalar(select(Role).where(Role.name == body.name)):
        raise ApiError(409, "ALREADY_EXISTS", f"Role '{body.name}' already exists")
    perms = _resolve_permissions(db, body.permission_keys)  # 400 if a key doesn't exist
    _guard_grantable(actor, [p.key for p in perms])  # 403 if the actor doesn't hold it
    role = Role(
        name=body.name,
        description=body.description,
        is_system=False,
        permissions=perms,
    )
    db.add(role)
    try:
        db.flush()
        add_security_event(
            db,
            event_type="rbac.role.created",
            outcome="success",
            request=request,
            actor_user_id=actor.id,
            target_type="role",
            target_id=role.id,
            metadata={"permissions": summarize_string_set(role.permission_keys)},
        )
        db.commit()
    except IntegrityError:
        db.rollback()
        raise ApiError(409, "ALREADY_EXISTS", f"Role '{body.name}' already exists") from None
    db.refresh(role)
    return _role_out(db, role)


@router.patch("/roles/{role_id}", response_model=RoleOut)
def update_role(
    role_id: int,
    body: RoleUpdate,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(ROLES_MANAGE)),
) -> RoleOut:
    _lock_site_settings(db)
    role = db.scalar(select(Role).where(Role.id == role_id).with_for_update())
    if role is None:
        raise ApiError(404, "NOT_FOUND", "Role not found")
    # The admin role is fully locked — editing it could lock everyone out of administration.
    if role.name == ADMIN_ROLE:
        raise ApiError(403, "PROTECTED_ROLE", "The admin role cannot be modified")
    # Only full admins may edit any system role at all — otherwise a delegate could rewrite
    # the base 'user' role's permission set and mass-escalate everyone.
    if role.is_system and not _is_admin(actor):
        raise ApiError(403, "PROTECTED_ROLE", "System roles can only be edited by an admin")
    if role.is_system and body.name is not None and body.name != role.name:
        raise ApiError(403, "PROTECTED_ROLE", "System roles cannot be renamed")
    # A non-admin may not rewrite a (custom) role that grants permissions they don't hold —
    # otherwise a roles.manage delegate could strip/repurpose a role above their own privilege
    # and mass-revoke it from every holder.
    if not _is_admin(actor) and not role.permission_keys <= actor.permission_keys:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "You cannot modify a role that grants permissions you do not hold",
        )
    require_version(body.expected_version, role.version, "Role")

    previous_name = role.name
    previous_description = role.description
    previous_permissions = sorted(role.permission_keys)

    if body.name is not None and body.name != role.name:
        if db.scalar(select(Role).where(Role.name == body.name)):
            raise ApiError(409, "ALREADY_EXISTS", f"Role '{body.name}' already exists")
        role.name = body.name
    if body.description is not None:
        role.description = body.description
    if body.permission_keys is not None:
        perms = _resolve_permissions(db, body.permission_keys)  # 400 if a key doesn't exist
        _guard_grantable(actor, [p.key for p in perms])  # 403 if the actor doesn't hold it
        role.permissions = perms
    changed_fields = _changed_role_fields(
        role,
        previous_name=previous_name,
        previous_description=previous_description,
        previous_permissions=previous_permissions,
    )
    role.version += 1
    add_security_event(
        db,
        event_type="rbac.role.updated",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="role",
        target_id=role.id,
        metadata={
            "changed_fields": changed_fields,
            "before": {
                "name": previous_name,
                "description": previous_description,
                "permissions": summarize_string_set(previous_permissions),
            },
            "after": {
                "name": role.name,
                "description": role.description,
                "permissions": summarize_string_set(role.permission_keys),
            },
        },
    )
    db.commit()
    db.refresh(role)
    return _role_out(db, role)


@router.delete("/roles/{role_id}", status_code=204)
def delete_role(
    role_id: int,
    request: Request,
    expected_version: int | None = Query(default=None, ge=1),
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(ROLES_MANAGE)),
) -> Response:
    site = _lock_site_settings(db)
    role = db.scalar(select(Role).where(Role.id == role_id).with_for_update())
    if role is None:
        raise ApiError(404, "NOT_FOUND", "Role not found")
    if role.is_system:
        raise ApiError(403, "PROTECTED_ROLE", "System roles cannot be deleted")
    # A non-admin may not delete a role granting permissions they don't hold (above their level).
    if not _is_admin(actor) and not role.permission_keys <= actor.permission_keys:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "You cannot delete a role that grants permissions you do not hold",
        )
    in_use = db.scalar(select(func.count()).select_from(User).where(User.role_id == role.id)) or 0
    if in_use:
        raise ApiError(
            409, "ROLE_IN_USE", f"{in_use} user(s) still have this role; reassign them first"
        )
    if site.default_role_id == role.id:
        raise ApiError(
            409,
            "ROLE_IN_USE",
            "This role is the signup default; select another default role before deleting it",
        )
    require_version(expected_version, role.version, "Role")
    add_security_event(
        db,
        event_type="rbac.role.deleted",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="role",
        target_id=role.id,
        metadata={
            "name": role.name,
            "description": role.description,
            "permissions": summarize_string_set(role.permission_keys),
        },
    )
    db.delete(role)
    db.commit()
    return Response(status_code=204)
