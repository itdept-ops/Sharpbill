from datetime import datetime

from pydantic import BaseModel, Field

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
    role: str
    role_id: int
    permissions: list[str]
    is_active: bool
    is_approved: bool
    status: str  # active | pending | disabled
    identities: list[IdentityOut]
    auth_providers: list[str]
    created_at: datetime
    last_login_at: datetime | None
    last_seen_at: datetime | None
    online: bool

    @classmethod
    def from_user(cls, user: User, *, online: bool = False) -> "UserOut":
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
            role=user.role_name,
            role_id=user.role_id,
            permissions=sorted(user.permission_keys),
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
        )


class UserListOut(BaseModel):
    items: list[UserOut]
    total: int


class RoleAssignRequest(BaseModel):
    role_id: int


class StatusUpdateRequest(BaseModel):
    is_active: bool


class ProfileUpdate(BaseModel):
    display_name: str | None = Field(default=None, max_length=255)
    title: str | None = Field(default=None, max_length=120)
    department: str | None = Field(default=None, max_length=120)
    phone: str | None = Field(default=None, max_length=40)
    location: str | None = Field(default=None, max_length=120)
    timezone: str | None = Field(default=None, max_length=60)
    bio: str | None = Field(default=None, max_length=500)
