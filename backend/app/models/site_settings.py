from datetime import datetime

from sqlalchemy import CheckConstraint, DateTime, ForeignKey, String, text
from sqlalchemy.dialects.mysql import TINYINT
from sqlalchemy.orm import Mapped, mapped_column

from app.models.base import Base

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


class SiteSettings(Base):
    """Singleton row (id = 1) holding site-wide configuration."""

    __tablename__ = "site_settings"
    __table_args__ = (
        CheckConstraint("id = 1", name="singleton_id"),
        CheckConstraint(
            "signup_mode IN ('open', 'approval', 'closed')",
            name="signup_mode_valid",
        ),
        CheckConstraint(
            "allow_google = 1 OR allow_microsoft = 1",
            name="provider_available",
        ),
        CheckConstraint("allow_google IN (0, 1)", name="allow_google_boolean"),
        CheckConstraint("allow_microsoft IN (0, 1)", name="allow_microsoft_boolean"),
        CheckConstraint("calm_mode IN (0, 1)", name="calm_mode_boolean"),
        CheckConstraint("retention_hold IN (0, 1)", name="retention_hold_boolean"),
        CheckConstraint(
            "(retention_hold = 0 AND retention_hold_reference IS NULL) OR "
            "(retention_hold = 1 AND retention_hold_reference IS NOT NULL "
            "AND CHAR_LENGTH(TRIM(retention_hold_reference)) BETWEEN 1 AND 255)",
            name="retention_hold_state_valid",
        ),
        _TABLE_ARGS,
    )

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=False)  # always 1
    signup_mode: Mapped[str] = mapped_column(
        String(20), server_default="open"
    )  # open|approval|closed
    allow_google: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("1"))
    allow_microsoft: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("1"))
    default_role_id: Mapped[int] = mapped_column(ForeignKey("roles.id", ondelete="RESTRICT"))
    # Global "calm" mode: dims the code-rain and drops the scanline overlay for everyone.
    calm_mode: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("0"))
    # A global legal/operational hold pauses governed retention jobs. The required reference is
    # an external case/ticket identifier; detailed evidence belongs in the security-event log.
    retention_hold: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("0"))
    retention_hold_reference: Mapped[str | None] = mapped_column(String(255))
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False),
        server_default=text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
        server_onupdate=text("CURRENT_TIMESTAMP(6)"),
    )
