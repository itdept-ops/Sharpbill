from datetime import datetime

from pydantic import BaseModel, ConfigDict, EmailStr, Field, FiniteFloat, StrictBool

from app.models import UserSession


class TokenLoginRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    id_token: str = Field(min_length=1, max_length=16_384)
    legal_accepted: StrictBool
    legal_bundle_version: str = Field(min_length=1, max_length=64)


class DevLoginRequest(BaseModel):
    """Body for the dev-only /api/auth/dev endpoint. `role` is a role NAME (e.g. 'admin')."""

    model_config = ConfigDict(extra="forbid")
    email: EmailStr
    role: str | None = Field(default=None, min_length=1, max_length=49)
    display_name: str | None = Field(default=None, max_length=255)
    legal_accepted: StrictBool
    legal_bundle_version: str = Field(min_length=1, max_length=64)


class AuthConfig(BaseModel):
    google: bool
    microsoft: bool
    # OAuth client IDs identify this public application; returning them at runtime keeps the
    # static web image immutable and promotable across environments.
    google_client_id: str | None
    microsoft_client_id: str | None
    dev: bool
    calm: bool  # global calm/reduced-motion mode (admin-set site setting)


class LocationUpdate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    latitude: float = Field(ge=-90, le=90)
    longitude: float = Field(ge=-180, le=180)
    accuracy: FiniteFloat | None = Field(default=None, ge=0, le=100_000)


class SessionOut(BaseModel):
    id: int
    user_agent: str | None
    ip: str | None
    created_at: datetime
    last_seen_at: datetime | None
    current: bool  # is this the session making the request?

    @classmethod
    def from_row(
        cls, s: UserSession, *, current: bool, include_device_details: bool = True
    ) -> "SessionOut":
        # IP and user-agent together form sensitive device-identifying data. Callers mask both for
        # directory readers who are neither the session owner nor a user manager.
        return cls(
            id=s.id,
            user_agent=s.user_agent if include_device_details else None,
            ip=s.ip if include_device_details else None,
            created_at=s.created_at,
            last_seen_at=s.last_seen_at,
            current=current,
        )
