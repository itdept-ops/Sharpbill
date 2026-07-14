from datetime import datetime

from sqlalchemy import DateTime, ForeignKey, String, text
from sqlalchemy.dialects.mysql import TINYINT
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.models.base import Base
from app.models.role import Role

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
    # --- profile ---
    title: Mapped[str | None] = mapped_column(String(120))
    department: Mapped[str | None] = mapped_column(String(120))
    phone: Mapped[str | None] = mapped_column(String(40))
    location: Mapped[str | None] = mapped_column(String(120))
    timezone: Mapped[str | None] = mapped_column(String(60))
    bio: Mapped[str | None] = mapped_column(String(500))
    # --- access / lifecycle ---
    role_id: Mapped[int] = mapped_column(ForeignKey("roles.id", ondelete="RESTRICT"), index=True)
    is_active: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("1"))
    is_approved: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("1"))
    last_login_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    last_seen_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    session_valid_after: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False),
        server_default=text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
    )

    role: Mapped[Role] = relationship(lazy="selectin")
    identities: Mapped[list["UserIdentity"]] = relationship(  # noqa: F821
        back_populates="user", cascade="all, delete-orphan", lazy="selectin"
    )

    @property
    def role_name(self) -> str:
        return self.role.name

    @property
    def permission_keys(self) -> set[str]:
        return self.role.permission_keys

    @property
    def auth_providers(self) -> list[str]:
        seen: dict[str, None] = {}
        for ident in self.identities:
            seen.setdefault(ident.provider, None)
        return list(seen.keys())

    @property
    def status(self) -> str:
        if not self.is_approved:
            return "pending"
        return "active" if self.is_active else "disabled"
