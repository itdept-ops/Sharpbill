from datetime import datetime

from sqlalchemy import DateTime, ForeignKey, String, text
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
    __table_args__ = _TABLE_ARGS

    id: Mapped[int] = mapped_column(primary_key=True)  # always 1
    signup_mode: Mapped[str] = mapped_column(
        String(20), server_default="open"
    )  # open|approval|closed
    allow_google: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("1"))
    allow_microsoft: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("1"))
    default_role_id: Mapped[int] = mapped_column(ForeignKey("roles.id", ondelete="RESTRICT"))
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False),
        server_default=text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
    )
