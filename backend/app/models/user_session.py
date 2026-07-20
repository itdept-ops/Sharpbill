from datetime import UTC, datetime, timedelta

from sqlalchemy import DateTime, ForeignKey, Index, String, text
from sqlalchemy.orm import Mapped, mapped_column

from app.config import settings
from app.models.base import Base

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


def _default_expiry() -> datetime:
    """Store the configured JWT lifetime using the schema's UTC-naive convention."""
    return datetime.now(UTC).replace(tzinfo=None) + timedelta(seconds=settings.session_ttl_seconds)


class UserSession(Base):
    """One row per issued session (device), keyed on the session token's `jti`.

    Lets a single device be revoked without disturbing a user's other sessions — the per-device
    upgrade of the global kick epoch (users.session_valid_after).
    """

    __tablename__ = "user_sessions"
    __table_args__ = (
        # Supports active-session listing and bulk revocation without a filesort/table scan.
        Index(
            "ix_user_sessions_user_revoked_created",
            "user_id",
            "revoked_at",
            "created_at",
        ),
        # Supports bounded expiry cleanup independently of a user filter.
        Index("ix_user_sessions_expires_at", "expires_at"),
        # Revocation retention is global, so it needs a leading revoked_at access path rather
        # than the user-leading active-session index above.
        Index("ix_user_sessions_revoked_at", "revoked_at"),
        _TABLE_ARGS,
    )

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"))
    jti: Mapped[str] = mapped_column(String(36), unique=True)
    user_agent: Mapped[str | None] = mapped_column(String(400))
    ip: Mapped[str | None] = mapped_column(String(45))
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=False), default=_default_expiry)
    last_seen_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    revoked_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
