from datetime import datetime

from pydantic import BaseModel, EmailStr, Field

from app.models import UserSession


class TokenLoginRequest(BaseModel):
    id_token: str = Field(min_length=1)


class DevLoginRequest(BaseModel):
    """Body for the dev-only /api/auth/dev endpoint. `role` is a role NAME (e.g. 'admin')."""

    email: EmailStr
    role: str | None = None
    display_name: str | None = None


class AuthConfig(BaseModel):
    google: bool
    microsoft: bool
    dev: bool
    calm: bool  # global calm/reduced-motion mode (admin-set site setting)


class LocationUpdate(BaseModel):
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
    def from_row(cls, s: UserSession, *, current: bool) -> "SessionOut":
        return cls(
            id=s.id,
            user_agent=s.user_agent,
            ip=s.ip,
            created_at=s.created_at,
            last_seen_at=s.last_seen_at,
            current=current,
        )
