from pydantic import BaseModel, EmailStr, Field


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


class LocationUpdate(BaseModel):
    latitude: float = Field(ge=-90, le=90)
    longitude: float = Field(ge=-180, le=180)
    accuracy: float | None = Field(default=None, ge=0)
