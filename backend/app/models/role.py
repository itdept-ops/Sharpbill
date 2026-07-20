from datetime import datetime

from sqlalchemy import CheckConstraint, DateTime, Integer, String, text
from sqlalchemy.dialects.mysql import TINYINT
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.models.base import Base
from app.models.permission import Permission
from app.models.role_permission import role_permissions

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


class Role(Base):
    __tablename__ = "roles"
    __table_args__ = (CheckConstraint("is_system IN (0, 1)", name="is_system_boolean"), _TABLE_ARGS)

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    name: Mapped[str] = mapped_column(String(50), unique=True)
    description: Mapped[str | None] = mapped_column(String(255))
    # System roles (admin/user) cannot be renamed or deleted through the API.
    is_system: Mapped[bool] = mapped_column(TINYINT(1), server_default=text("0"))
    version: Mapped[int] = mapped_column(Integer, nullable=False, server_default=text("1"))
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False),
        server_default=text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
        server_onupdate=text("CURRENT_TIMESTAMP(6)"),
    )

    permissions: Mapped[list[Permission]] = relationship(
        secondary=role_permissions, lazy="selectin", order_by=Permission.key
    )

    @property
    def permission_keys(self) -> set[str]:
        return {p.key for p in self.permissions}
