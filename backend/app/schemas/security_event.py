from datetime import datetime
from typing import Any

from pydantic import BaseModel


class SecurityEventOut(BaseModel):
    id: int
    event_type: str
    outcome: str
    severity: str
    request_id: str | None
    actor_user_id: int | None
    target_type: str | None
    target_id: str | None
    source_ip: str | None
    metadata: dict[str, Any]
    occurred_at: datetime
    retention_until: datetime
    delivery_status: str
    delivery_attempts: int
    delivered_at: datetime | None


class SecurityEventListOut(BaseModel):
    items: list[SecurityEventOut]
    next_cursor: int | None
