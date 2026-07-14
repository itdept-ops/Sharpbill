from datetime import datetime

from pydantic import BaseModel


class PresenceUser(BaseModel):
    id: int
    email: str
    display_name: str | None
    role: str
    last_seen_at: datetime | None


class PresenceOut(BaseModel):
    online: list[PresenceUser]
    count: int
    window_seconds: int
