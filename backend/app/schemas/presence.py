from datetime import datetime

from pydantic import BaseModel


class PresenceUser(BaseModel):
    # Deliberately omits email — presence.view exposes who is online, not the directory.
    id: int
    display_name: str | None
    role: str
    last_seen_at: datetime | None


class PresenceOut(BaseModel):
    online: list[PresenceUser]
    count: int
    window_seconds: int
