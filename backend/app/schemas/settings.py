from datetime import datetime
from typing import Literal

from pydantic import BaseModel, ConfigDict

SignupMode = Literal["open", "approval", "closed"]


class SiteSettingsOut(BaseModel):
    signup_mode: SignupMode
    allow_google: bool
    allow_microsoft: bool
    default_role_id: int
    default_role_name: str
    calm_mode: bool
    updated_at: datetime


class SiteSettingsUpdate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    signup_mode: SignupMode | None = None
    allow_google: bool | None = None
    allow_microsoft: bool | None = None
    default_role_id: int | None = None
    calm_mode: bool | None = None
