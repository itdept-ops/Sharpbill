from datetime import datetime

from sqlalchemy import BigInteger, DateTime, Index, Integer, String, text
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
    __table_args__ = (
        # Log browsing filters by one of these columns and orders newest IDs first.
        Index("ix_request_logs_user_id_id", "user_id", "id"),
        Index("ix_request_logs_method_id", "method", "id"),
        _TABLE_ARGS,
    )

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    method: Mapped[str] = mapped_column(String(10))
    path: Mapped[str] = mapped_column(String(255))
    # No FK: keep logs if a user is deleted. The composite user_id/id index covers both exact
    # user filtering and newest-first pagination, superseding the legacy single-column index.
    user_id: Mapped[int | None] = mapped_column(Integer)
    ip: Mapped[str | None] = mapped_column(String(45))
    status_code: Mapped[int] = mapped_column(Integer)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), index=True, server_default=text("CURRENT_TIMESTAMP(6)")
    )
