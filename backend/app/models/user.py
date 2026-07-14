from datetime import datetime

from sqlalchemy import DateTime, String, text
from sqlalchemy.dialects.mysql import TINYINT
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.models.base import Base

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


class User(Base):
    __tablename__ = "users"
    __table_args__ = _TABLE_ARGS

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    email: Mapped[str] = mapped_column(String(255), index=True)  # lowercase; not unique
    display_name: Mapped[str | None] = mapped_column(String(255))
    role: Mapped[str] = mapped_column(String(20), server_default="user")  # 'admin' | 'user'
    is_active: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("1"))
    last_login_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False),
        server_default=text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
    )

    identities: Mapped[list["UserIdentity"]] = relationship(  # noqa: F821
        back_populates="user", cascade="all, delete-orphan", lazy="selectin"
    )

    @property
    def auth_providers(self) -> list[str]:
        # Distinct providers, preserving first-seen order.
        seen: dict[str, None] = {}
        for ident in self.identities:
            seen.setdefault(ident.provider, None)
        return list(seen.keys())
