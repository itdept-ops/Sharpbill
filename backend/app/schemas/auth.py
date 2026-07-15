from datetime import datetime

from pydantic import BaseModel, ConfigDict, EmailStr, Field

from app.models import UserSession


class TokenLoginRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id_token: str = Field(min_length=1)


class DevLoginRequest(BaseModel):
    """Body for the dev-only /api/auth/dev endpoint. `role` is a role NAME (e.g. 'admin')."""

    model_config = ConfigDict(extra="forbid")
    email: EmailStr
    role: str | None = None
    display_name: str | None = None


class AuthConfig(BaseModel):
    google: bool
    microsoft: bool
    dev: bool
    calm: bool  # global calm/reduced-motion mode (admin-set site setting)


class LocationUpdate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    latitude: float = Field(ge=-90, le=90)
    longitude: float = Field(ge=-180, le=180)
    accuracy: float | None = Field(default=None, ge=0)


class SessionOut(BaseModel):
    id: int
    user_agent: str | None
    ip: str | None
    created_at: datetime
    last_seen_at: datetime | None
    current: bool  # is this the session making the request?

    @classmethod
    def from_row(cls, s: UserSession, *, current: bool, include_ip: bool = True) -> "SessionOut":
        # The IP is location-adjacent PII: callers pass include_ip=False to mask it for viewers
        # who may only see their own sessions' source (mirrors the GPS include_location gating).
        return cls(
            id=s.id,
            user_agent=s.user_agent,
            ip=s.ip if include_ip else None,
            created_at=s.created_at,
            last_seen_at=s.last_seen_at,
            current=current,
        )
