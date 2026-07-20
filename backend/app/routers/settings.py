from typing import cast

from fastapi import APIRouter, Depends, Request
from sqlalchemy.orm import Session

from app.account_lifecycle import lock_current_role, lock_current_site_settings
from app.admin_access import administration_available
from app.auth.deps import require_permission
from app.auth.service import get_site_settings
from app.config import settings
from app.db import get_db
from app.errors import ApiError
from app.models import Role, SiteSettings, User
from app.permissions import ADMIN_ROLE, ROLES_MANAGE, SETTINGS_MANAGE
from app.schemas.settings import SignupMode, SiteSettingsOut, SiteSettingsUpdate
from app.security_events import add_security_event

router = APIRouter()


def _out(db: Session, s: SiteSettings) -> SiteSettingsOut:
    role = db.get(Role, s.default_role_id)
    return SiteSettingsOut(
        signup_mode=cast(SignupMode, s.signup_mode),
        allow_google=s.allow_google,
        allow_microsoft=s.allow_microsoft,
        default_role_id=s.default_role_id,
        default_role_name=role.name if role else "",
        calm_mode=s.calm_mode,
        updated_at=s.updated_at,
    )


def _validate_provider_transition(db: Session, s: SiteSettings, data: dict) -> None:
    requested_google = data.get("allow_google")
    requested_microsoft = data.get("allow_microsoft")
    changed = any(
        field in data and data[field] is not None for field in ("allow_google", "allow_microsoft")
    )
    if not changed:
        return
    new_google = bool(s.allow_google if requested_google is None else requested_google)
    new_microsoft = bool(s.allow_microsoft if requested_microsoft is None else requested_microsoft)
    effective_google = new_google and settings.google_provider_configured
    effective_microsoft = new_microsoft and settings.microsoft_provider_configured
    if not (effective_google or effective_microsoft):
        raise ApiError(
            400,
            "NO_PROVIDER_ENABLED",
            "At least one configured sign-in provider must stay enabled",
        )
    if not administration_available(
        db,
        google=effective_google,
        microsoft=effective_microsoft,
        dev=settings.is_dev_auth_enabled,
        lock=True,
    ):
        raise ApiError(
            400,
            "ADMIN_ACCESS_STRANDED",
            "At least one enabled provider must retain an administrator or bootstrap path",
        )


@router.get("/settings", response_model=SiteSettingsOut)
def read_settings(
    db: Session = Depends(get_db), _: User = Depends(require_permission(SETTINGS_MANAGE))
) -> SiteSettingsOut:
    return _out(db, get_site_settings(db))


@router.put("/settings", response_model=SiteSettingsOut)
def update_settings(
    body: SiteSettingsUpdate,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(SETTINGS_MANAGE)),
) -> SiteSettingsOut:
    # Serialize updates to the singleton. Without a locking read, two admins can each
    # disable a different provider after observing the same old row and jointly commit a
    # state with no sign-in provider enabled.
    s = lock_current_site_settings(db)
    if s is None:
        raise ApiError(500, "SETTINGS_NOT_INITIALIZED", "Site settings are not initialized")
    data = body.model_dump(exclude_unset=True)
    before = {
        "signup_mode": s.signup_mode,
        "allow_google": s.allow_google,
        "allow_microsoft": s.allow_microsoft,
        "default_role_id": s.default_role_id,
        "calm_mode": s.calm_mode,
    }
    if data.get("default_role_id") is not None:
        if ROLES_MANAGE not in actor.permission_keys:
            raise ApiError(
                403,
                "INSUFFICIENT_PRIVILEGE",
                "Changing the signup default requires both settings.manage and roles.manage",
            )
        role = lock_current_role(db, data["default_role_id"])
        if role is None:
            raise ApiError(400, "UNKNOWN_ROLE", "No such role")
        if role.name == ADMIN_ROLE:
            raise ApiError(
                403,
                "PROTECTED_DEFAULT_ROLE",
                "The administrator role cannot be used as the signup default",
            )
        # Amplification guard: the org-wide default signup role can't grant more than the actor
        # holds — otherwise settings.manage would be a backdoor to minting admins on sign-up.
        if actor.role_name != ADMIN_ROLE and not role.permission_keys <= actor.permission_keys:
            raise ApiError(
                403,
                "INSUFFICIENT_PRIVILEGE",
                "You cannot set a default role with permissions you do not hold",
            )
    # Recheck the merged effective-provider state only when a provider toggle changes. Missing
    # credentials must not block unrelated administration such as calm mode or signup policy.
    _validate_provider_transition(db, s, data)
    for field, value in data.items():
        if value is not None:
            setattr(s, field, value)
    changes = {
        field: {"from": before[field], "to": getattr(s, field)}
        for field in before
        if before[field] != getattr(s, field)
    }
    add_security_event(
        db,
        event_type="settings.updated",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="site_settings",
        target_id=s.id,
        metadata={"changes": changes},
    )
    db.commit()
    return _out(db, s)
