from datetime import datetime

from pydantic import BaseModel


class RequestLogOut(BaseModel):
    id: int
    method: str
    path: str
    user_id: int | None
    user_email: str | None
    ip: str | None
    status_code: int
    created_at: datetime


class RequestLogListOut(BaseModel):
    items: list[RequestLogOut]
    total: int
    next_cursor: int | None
