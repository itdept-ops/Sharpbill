from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict

from app.models import User


class UserOut(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: int
    email: str
    display_name: str | None
    role: str
    is_active: bool
    auth_providers: list[str]
    created_at: datetime
    last_login_at: datetime | None

    @classmethod
    def from_user(cls, user: User) -> "UserOut":
        return cls.model_validate(user)


class UserListOut(BaseModel):
    items: list[UserOut]
    total: int


class RoleUpdateRequest(BaseModel):
    role: Literal["admin", "user"]


class StatusUpdateRequest(BaseModel):
    is_active: bool
