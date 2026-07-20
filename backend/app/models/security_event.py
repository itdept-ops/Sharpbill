from datetime import datetime

from sqlalchemy import JSON, BigInteger, DateTime, ForeignKey, Index, Integer, String, text
from sqlalchemy import event as sqlalchemy_event
from sqlalchemy.orm import Mapped, mapped_column

from app.models.base import Base

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


class SecurityEvent(Base):
    """Immutable security fact queued for delivery to an external audit/SIEM sink.

    Delivery state deliberately lives in ``security_event_deliveries``. The application never
    updates an event after insertion, so dispatcher retries cannot rewrite the original fact.
    Actor/target identifiers intentionally have no foreign keys: evidence survives account and
    role deletion.
    """

    __tablename__ = "security_events"
    __table_args__ = (
        Index("ix_security_events_occurred_id", "occurred_at", "id"),
        Index("ix_security_events_type_id", "event_type", "id"),
        Index("ix_security_events_actor_id", "actor_user_id", "id"),
        Index("ix_security_events_request_id", "request_id"),
        Index("ix_security_events_retention_until", "retention_until"),
        _TABLE_ARGS,
    )

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    event_type: Mapped[str] = mapped_column(String(80))
    outcome: Mapped[str] = mapped_column(String(16))
    severity: Mapped[str] = mapped_column(String(16))
    request_id: Mapped[str | None] = mapped_column(String(64))
    actor_user_id: Mapped[int | None] = mapped_column(Integer)
    target_type: Mapped[str | None] = mapped_column(String(40))
    target_id: Mapped[str | None] = mapped_column(String(128))
    source_ip: Mapped[str | None] = mapped_column(String(45))
    event_metadata: Mapped[dict] = mapped_column("metadata", JSON)
    occurred_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    retention_until: Mapped[datetime] = mapped_column(DateTime(timezone=False))


class SecurityEventDelivery(Base):
    """Mutable delivery cursor for a SecurityEvent; consumed by a future SIEM worker."""

    __tablename__ = "security_event_deliveries"
    __table_args__ = (
        Index(
            "ix_security_event_deliveries_dispatch",
            "status",
            "next_attempt_at",
            "event_id",
        ),
        Index("ix_security_event_deliveries_lease", "lease_expires_at"),
        _TABLE_ARGS,
    )

    event_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("security_events.id", ondelete="CASCADE"),
        primary_key=True,
    )
    status: Mapped[str] = mapped_column(String(16), server_default="pending")
    attempts: Mapped[int] = mapped_column(Integer, server_default="0")
    next_attempt_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    lease_owner: Mapped[str | None] = mapped_column(String(64))
    lease_expires_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    last_attempt_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    delivered_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    last_error: Mapped[str | None] = mapped_column(String(255))
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False),
        server_default=text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
        server_onupdate=text("CURRENT_TIMESTAMP(6)"),
    )


@sqlalchemy_event.listens_for(SecurityEvent, "before_update")
@sqlalchemy_event.listens_for(SecurityEvent, "before_delete")
def _prevent_orm_rewrite(*_args) -> None:
    raise TypeError("SecurityEvent facts are append-only; mutate delivery state instead")
