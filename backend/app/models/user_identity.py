from datetime import datetime
from typing import TYPE_CHECKING

from sqlalchemy import DateTime, ForeignKey, String, UniqueConstraint, text
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.models.base import Base

if TYPE_CHECKING:
    from app.models.user import User


class UserIdentity(Base):
    __tablename__ = "user_identities"
    __table_args__ = (
        UniqueConstraint(
            "provider",
            "provider_namespace",
            "provider_subject",
            name="uq_user_identities_provider_namespace_subject",
        ),
        {
            "mysql_engine": "InnoDB",
            "mysql_charset": "utf8mb4",
            "mysql_collate": "utf8mb4_0900_ai_ci",
        },
    )

    id: Mapped[int] = mapped_column(primary_key=True, autoincrement=True)
    user_id: Mapped[int] = mapped_column(ForeignKey("users.id", ondelete="CASCADE"), index=True)
    provider: Mapped[str] = mapped_column(String(20))  # 'google' | 'microsoft' | 'dev'
    # Microsoft object IDs are tenant-scoped, so broad onboarding keys Microsoft identities by
    # (tid, oid). Google sub and local dev identities use the empty global namespace.
    provider_namespace: Mapped[str] = mapped_column(
        String(255, collation="utf8mb4_0900_bin"), default="", server_default=text("''")
    )
    # OIDC `sub` is opaque and case-sensitive; never inherit the table's ai_ci collation for
    # equality or the provider+subject uniqueness contract.
    provider_subject: Mapped[str] = mapped_column(
        String(255, collation="utf8mb4_0900_bin")
    )  # Google sub / Microsoft oid
    # Last signature-verified provider context. These values are audit/recovery evidence, not an
    # onboarding or authorization boundary; signup mode and account/RBAC state govern access.
    provider_tenant_id: Mapped[str | None] = mapped_column(String(255))  # Microsoft signed `tid`
    provider_hosted_domain: Mapped[str | None] = mapped_column(String(255))  # Google signed `hd`
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False),
        server_default=text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
        server_onupdate=text("CURRENT_TIMESTAMP(6)"),
    )

    user: Mapped["User"] = relationship(back_populates="identities")  # noqa: F821
