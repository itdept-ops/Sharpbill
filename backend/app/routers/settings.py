from fastapi import APIRouter, Depends
from sqlalchemy.orm import Session

from app.auth.deps import require_permission
from app.auth.service import get_site_settings
from app.db import get_db
from app.errors import ApiError
from app.models import Role, SiteSettings, User
from app.permissions import ADMIN_ROLE, SETTINGS_MANAGE
from app.schemas.settings import SiteSettingsOut, SiteSettingsUpdate

router = APIRouter()


def _out(db: Session, s: SiteSettings) -> SiteSettingsOut:
    role = db.get(Role, s.default_role_id)
    return SiteSettingsOut(
        signup_mode=s.signup_mode,
        allow_google=s.allow_google,
        allow_microsoft=s.allow_microsoft,
        default_role_id=s.default_role_id,
        default_role_name=role.name if role else "",
        updated_at=s.updated_at,
    )


@router.get("/settings", response_model=SiteSettingsOut)
def read_settings(
    db: Session = Depends(get_db), _: User = Depends(require_permission(SETTINGS_MANAGE))
) -> SiteSettingsOut:
    return _out(db, get_site_settings(db))


@router.put("/settings", response_model=SiteSettingsOut)
def update_settings(
    body: SiteSettingsUpdate,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(SETTINGS_MANAGE)),
) -> SiteSettingsOut:
    s = get_site_settings(db)
    data = body.model_dump(exclude_unset=True)
    if data.get("default_role_id") is not None:
        role = db.get(Role, data["default_role_id"])
        if role is None:
            raise ApiError(400, "UNKNOWN_ROLE", "No such role")
        # Amplification guard: the org-wide default signup role can't grant more than the actor
        # holds — otherwise settings.manage would be a backdoor to minting admins on sign-up.
        if actor.role_name != ADMIN_ROLE and not role.permission_keys <= actor.permission_keys:
            raise ApiError(
                403,
                "INSUFFICIENT_PRIVILEGE",
                "You cannot set a default role with permissions you do not hold",
            )
    for field, value in data.items():
        if value is not None:
            setattr(s, field, value)
    db.commit()
    return _out(db, s)
