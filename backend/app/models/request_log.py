from datetime import datetime

from sqlalchemy import DateTime, Integer, String, text
from sqlalchemy.orm import Mapped, mapped_column

from app.models.base import Base

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


class RequestLog(Base):
    """One row per meaningful API request: endpoint, user, and IP."""

    __tablename__ = "request_logs"
    __table_args__ = _TABLE_ARGS

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    method: Mapped[str] = mapped_column(String(10))
    path: Mapped[str] = mapped_column(String(255))
    user_id: Mapped[int | None] = mapped_column(
        Integer, index=True
    )  # no FK: keep logs if user deleted
    ip: Mapped[str | None] = mapped_column(String(45))
    status_code: Mapped[int] = mapped_column(Integer)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), index=True, server_default=text("CURRENT_TIMESTAMP(6)")
    )
