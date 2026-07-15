import re

from pydantic import BaseModel, ConfigDict, Field, field_validator

_KEY_RE = re.compile(r"^[a-z][a-z0-9]*(\.[a-z0-9]+)+$")  # e.g. "reports.export"
_NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _-]{0,48}$")


class PermissionOut(BaseModel):
    id: int
    key: str
    description: str | None
    is_system: bool


class PermissionCreate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    key: str = Field(max_length=100)
    description: str | None = Field(default=None, max_length=255)

    @field_validator("key")
    @classmethod
    def _valid_key(cls, v: str) -> str:
        v = v.strip().lower()
        if not _KEY_RE.match(v):
            raise ValueError("key must look like 'area.action' (lowercase, dot-separated)")
        return v


class RoleOut(BaseModel):
    id: int
    name: str
    description: str | None
    is_system: bool
    permissions: list[PermissionOut]
    user_count: int


class RoleCreate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str
    description: str | None = Field(default=None, max_length=255)
    permission_keys: list[str] = Field(default_factory=list, max_length=100)

    @field_validator("name")
    @classmethod
    def _valid_name(cls, v: str) -> str:
        v = v.strip()
        if not _NAME_RE.match(v):
            raise ValueError("name must be 1-49 chars: letters, digits, space, _ or -")
        return v


class RoleUpdate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    name: str | None = None
    description: str | None = Field(default=None, max_length=255)
    permission_keys: list[str] | None = Field(default=None, max_length=100)

    @field_validator("name")
    @classmethod
    def _valid_name(cls, v: str | None) -> str | None:
        if v is None:
            return None
        v = v.strip()
        if not _NAME_RE.match(v):
            raise ValueError("name must be 1-49 chars: letters, digits, space, _ or -")
        return v
