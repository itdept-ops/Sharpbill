from datetime import datetime
from typing import TYPE_CHECKING

from sqlalchemy import CheckConstraint, DateTime, Double, ForeignKey, Index, Integer, String, text
from sqlalchemy.dialects.mysql import JSON, TINYINT
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.models.base import Base
from app.models.permission import Permission
from app.models.role import Role
from app.models.user_permission import user_permissions

if TYPE_CHECKING:
    from app.models.user_identity import UserIdentity

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


class User(Base):
    __tablename__ = "users"
    __table_args__ = (
        # Directory/export endpoints use this stable ordering for every page.
        Index("ix_users_created_at_id", "created_at", "id"),
        CheckConstraint(
            "last_latitude IS NULL OR last_latitude BETWEEN -90 AND 90",
            name="last_latitude_valid",
        ),
        CheckConstraint(
            "last_longitude IS NULL OR last_longitude BETWEEN -180 AND 180",
            name="last_longitude_valid",
        ),
        CheckConstraint(
            "last_location_accuracy IS NULL OR last_location_accuracy BETWEEN 0 AND 100000",
            name="last_location_accuracy_valid",
        ),
        CheckConstraint("is_active IN (0, 1)", name="is_active_boolean"),
        CheckConstraint("is_approved IN (0, 1)", name="is_approved_boolean"),
        _TABLE_ARGS,
    )

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
    # Optimistic-concurrency token for role/direct-permission replacement operations.
    access_version: Mapped[int] = mapped_column(Integer, nullable=False, server_default=text("1"))
    last_login_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    last_seen_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False), index=True)
    session_valid_after: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    # Optional last-known location (only if the user opts in on login).
    last_latitude: Mapped[float | None] = mapped_column(Double)
    last_longitude: Mapped[float | None] = mapped_column(Double)
    last_location_accuracy: Mapped[float | None] = mapped_column(Double)
    last_location_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))
    # Per-user UI accent color (hex, e.g. "#35ff74"); null = the default green.
    accent_color: Mapped[str | None] = mapped_column(String(9))
    # Extensible per-user UI preferences bag (base tone, glow, motion, rain, density,
    # typography, accessibility). NULL / missing key => that axis renders at today's default.
    ui_prefs: Mapped[dict | None] = mapped_column(JSON)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False),
        server_default=text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
        server_onupdate=text("CURRENT_TIMESTAMP(6)"),
    )

    role: Mapped[Role] = relationship(lazy="selectin")
    # Lazy (not selectin): identities are only needed when a User is serialized to UserOut, not on
    # the per-request auth path (get_current_user loads the user for permission checks and never
    # reads identities). Call sites that serialize many users eager-load it explicitly.
    identities: Mapped[list["UserIdentity"]] = relationship(  # noqa: F821
        back_populates="user", cascade="all, delete-orphan", lazy="select"
    )
    # Permissions granted directly to this user, on top of their role (RBAC + per-user grants).
    granted_permissions: Mapped[list[Permission]] = relationship(
        secondary=user_permissions, lazy="selectin", order_by=Permission.key
    )

    @property
    def role_name(self) -> str:
        return self.role.name

    @property
    def direct_permission_keys(self) -> set[str]:
        return {p.key for p in self.granted_permissions}

    @property
    def permission_keys(self) -> set[str]:
        # Effective access = the role's permissions plus any granted directly to the user.
        return self.role.permission_keys | self.direct_permission_keys

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
