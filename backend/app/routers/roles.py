from fastapi import APIRouter, Depends, Response
from sqlalchemy import func, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.auth.deps import require_permission
from app.db import get_db
from app.errors import ApiError
from app.models import Permission, Role, User
from app.permissions import ADMIN_ROLE, ROLES_MANAGE
from app.schemas.role import (
    PermissionCreate,
    PermissionOut,
    RoleCreate,
    RoleOut,
    RoleUpdate,
)

router = APIRouter()


def _is_admin(user: User) -> bool:
    return user.role_name == ADMIN_ROLE


def _role_out(db: Session, role: Role) -> RoleOut:
    count = db.scalar(select(func.count()).select_from(User).where(User.role_id == role.id)) or 0
    return RoleOut(
        id=role.id,
        name=role.name,
        description=role.description,
        is_system=role.is_system,
        permissions=[
            PermissionOut.model_validate(p, from_attributes=True) for p in role.permissions
        ],
        user_count=count,
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

    (A full admin holds every permission, so this is a no-op for them. It stops a delegate
    with roles.manage from minting a role that carries permissions they lack and climbing.)
    """
    extra = set(_normalize(keys)) - actor.permission_keys
    if extra:
        raise ApiError(
            403,
            "INSUFFICIENT_PRIVILEGE",
            "You can only grant permissions you hold; missing: " + ", ".join(sorted(extra)),
        )


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
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(ROLES_MANAGE)),
) -> PermissionOut:
    if db.scalar(select(Permission).where(Permission.key == body.key)):
        raise ApiError(409, "ALREADY_EXISTS", f"Permission '{body.key}' already exists")
    perm = Permission(key=body.key, description=body.description, is_system=False)
    db.add(perm)
    try:
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
    roles = db.scalars(select(Role).order_by(Role.id)).all()
    return [_role_out(db, r) for r in roles]


@router.post("/roles", response_model=RoleOut, status_code=201)
def create_role(
    body: RoleCreate,
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
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(ROLES_MANAGE)),
) -> RoleOut:
    role = db.get(Role, role_id)
    if role is None:
        raise ApiError(404, "NOT_FOUND", "Role not found")
    # The admin role is fully locked — editing it could lock everyone out of administration.
    if role.name == ADMIN_ROLE:
        raise ApiError(403, "PROTECTED_ROLE", "The admin role cannot be modified")
    # Only full admins may edit any system role at all — otherwise a delegate could rewrite
    # the base 'user' role's permission set and mass-escalate everyone.
    if role.is_system and not _is_admin(actor):
        raise ApiError(403, "PROTECTED_ROLE", "System roles can only be edited by an admin")

    if body.name is not None and body.name != role.name:
        if role.is_system:
            raise ApiError(403, "PROTECTED_ROLE", "System roles cannot be renamed")
        if db.scalar(select(Role).where(Role.name == body.name)):
            raise ApiError(409, "ALREADY_EXISTS", f"Role '{body.name}' already exists")
        role.name = body.name
    if body.description is not None:
        role.description = body.description
    if body.permission_keys is not None:
        perms = _resolve_permissions(db, body.permission_keys)  # 400 if a key doesn't exist
        _guard_grantable(actor, [p.key for p in perms])  # 403 if the actor doesn't hold it
        role.permissions = perms
    db.commit()
    db.refresh(role)
    return _role_out(db, role)


@router.delete("/roles/{role_id}", status_code=204)
def delete_role(
    role_id: int,
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(ROLES_MANAGE)),
) -> Response:
    role = db.get(Role, role_id)
    if role is None:
        raise ApiError(404, "NOT_FOUND", "Role not found")
    if role.is_system:
        raise ApiError(403, "PROTECTED_ROLE", "System roles cannot be deleted")
    in_use = db.scalar(select(func.count()).select_from(User).where(User.role_id == role.id)) or 0
    if in_use:
        raise ApiError(
            409, "ROLE_IN_USE", f"{in_use} user(s) still have this role; reassign them first"
        )
    db.delete(role)
    db.commit()
    return Response(status_code=204)
