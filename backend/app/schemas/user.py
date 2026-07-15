from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

from app.models import User


class IdentityOut(BaseModel):
    provider: str
    # The immutable provider subject id (Google `sub` / Microsoft `oid`). Identity is keyed
    # on this, never on email, so a changed provider email can't impersonate another account.
    subject: str


class UserOut(BaseModel):
    id: int
    email: str
    display_name: str | None
    title: str | None
    department: str | None
    phone: str | None
    location: str | None
    timezone: str | None
    bio: str | None
    accent_color: str | None
    ui_prefs: dict | None
    role: str
    role_id: int
    permissions: list[str]  # effective = role ∪ direct grants
    role_permissions: list[str]  # inherited from the role
    direct_permissions: list[str]  # granted directly to this user
    is_active: bool
    is_approved: bool
    status: str  # active | pending | disabled
    identities: list[IdentityOut]
    auth_providers: list[str]
    created_at: datetime
    last_login_at: datetime | None
    last_seen_at: datetime | None
    online: bool
    last_latitude: float | None
    last_longitude: float | None
    last_location_accuracy: float | None
    last_location_at: datetime | None

    @classmethod
    def from_user(
        cls, user: User, *, online: bool = False, include_location: bool = True
    ) -> "UserOut":
        # Location is opt-in GPS and privacy-sensitive: callers pass include_location=False to
        # strip it for viewers who may only see their own coordinates.
        return cls(
            id=user.id,
            email=user.email,
            display_name=user.display_name,
            title=user.title,
            department=user.department,
            phone=user.phone,
            location=user.location,
            timezone=user.timezone,
            bio=user.bio,
            accent_color=user.accent_color,
            ui_prefs=user.ui_prefs,
            role=user.role_name,
            role_id=user.role_id,
            permissions=sorted(user.permission_keys),
            role_permissions=sorted(user.role.permission_keys),
            direct_permissions=sorted(user.direct_permission_keys),
            is_active=user.is_active,
            is_approved=user.is_approved,
            status=user.status,
            identities=[
                IdentityOut(provider=i.provider, subject=i.provider_subject)
                for i in user.identities
            ],
            auth_providers=user.auth_providers,
            created_at=user.created_at,
            last_login_at=user.last_login_at,
            last_seen_at=user.last_seen_at,
            online=online,
            last_latitude=user.last_latitude if include_location else None,
            last_longitude=user.last_longitude if include_location else None,
            last_location_accuracy=user.last_location_accuracy if include_location else None,
            last_location_at=user.last_location_at if include_location else None,
        )


class UserListOut(BaseModel):
    items: list[UserOut]
    total: int


class RoleAssignRequest(BaseModel):
    role_id: int


class StatusUpdateRequest(BaseModel):
    is_active: bool


class PermissionGrantRequest(BaseModel):
    """The full set of permissions granted DIRECTLY to a user (replaces the existing grants)."""

    permission_keys: list[str] = Field(default_factory=list)


class BulkActionRequest(BaseModel):
    ids: list[int] = Field(min_length=1, max_length=500)
    action: Literal["activate", "deactivate", "approve", "assign_role"]
    role_id: int | None = None


class UiPrefs(BaseModel):
    """Per-user UI customization axes. Every field is optional so a PATCH can carry a single
    key; the router MERGES incoming keys into the stored bag. Values flow into
    documentElement style/dataset on the client, so each is strictly enum/range-validated and
    unknown keys are rejected — no free strings reach the DOM. A missing key renders at today's
    default on the frontend."""

    model_config = ConfigDict(extra="forbid")

    # Color
    base_tone: Literal["abyss", "ink", "graphite", "midnight", "warm-black"] | None = None
    background_depth: Literal["pure-black", "standard", "elevated"] | None = None
    border_glow: Literal["hairline", "standard", "neon"] | None = None
    # Glow & texture
    glow_intensity: Literal["off", "subtle", "normal", "intense"] | None = None
    scanlines: Literal["off", "subtle", "standard", "heavy"] | None = None
    corner_radius: Literal["sharp", "soft", "round"] | None = None
    # Motion & rain
    motion: Literal["full", "calm", "reduced"] | None = None
    rain_density: float | None = Field(default=None, ge=0, le=0.8)
    rain_speed: Literal["still", "slow", "normal", "fast"] | None = None
    rain_glyphs: Literal["katakana", "ascii", "binary", "hex"] | None = None
    # Typography & density
    font_family: (
        Literal["system", "high-legibility", "cascadia", "jetbrains", "consolas", "menlo"] | None
    ) = None
    text_scale: Literal["90", "100", "112", "125"] | None = None
    density: Literal["compact", "comfortable", "spacious"] | None = None
    # Accessibility
    high_contrast_text: bool | None = None
    reduce_transparency: bool | None = None
    focus_ring: Literal["standard", "bold", "high-contrast"] | None = None
    zebra_rows: bool | None = None
    link_underlines: bool | None = None
    # Schema version for future client-side upgrade shims.
    v: int | None = None


class ProfileUpdate(BaseModel):
    display_name: str | None = Field(default=None, max_length=255)
    title: str | None = Field(default=None, max_length=120)
    department: str | None = Field(default=None, max_length=120)
    phone: str | None = Field(default=None, max_length=40)
    location: str | None = Field(default=None, max_length=120)
    timezone: str | None = Field(default=None, max_length=60)
    bio: str | None = Field(default=None, max_length=500)
    accent_color: str | None = Field(default=None, pattern=r"^#[0-9a-fA-F]{6}$")
    # A single-key PATCH merges into the stored bag; explicit null clears all prefs to defaults.
    ui_prefs: UiPrefs | None = None
