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
