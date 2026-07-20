from datetime import datetime

from sqlalchemy import BigInteger, CheckConstraint, DateTime, ForeignKey, Index, Integer, String
from sqlalchemy import event as sqlalchemy_event
from sqlalchemy.orm import Mapped, mapped_column

from app.models.base import Base

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


class LegalAcceptance(Base):
    """Append-only evidence that a principal accepted one exact legal-document bundle.

    Device metadata is deliberately bounded and nullable. Account anonymization uses the single
    governed Core update in ``privacy_lifecycle`` to remove that direct personal data while
    retaining the contract versions, time, and pseudonymous internal principal reference.
    """

    __tablename__ = "legal_acceptances"
    __table_args__ = (
        Index("ix_legal_acceptances_user_accepted_id", "user_id", "accepted_at", "id"),
        Index("ix_legal_acceptances_accepted_id", "accepted_at", "id"),
        Index("ix_legal_acceptances_retention_id", "retention_until", "id"),
        CheckConstraint(
            "terms_sha256 REGEXP '^[0-9a-f]{64}$'",
            name="terms_sha256_valid",
        ),
        CheckConstraint(
            "eula_sha256 REGEXP '^[0-9a-f]{64}$'",
            name="eula_sha256_valid",
        ),
        CheckConstraint(
            "acceptable_use_sha256 REGEXP '^[0-9a-f]{64}$'",
            name="acceptable_use_sha256_valid",
        ),
        CheckConstraint(
            "privacy_sha256 REGEXP '^[0-9a-f]{64}$'",
            name="privacy_sha256_valid",
        ),
        CheckConstraint("retention_until > accepted_at", name="retention_after_acceptance"),
        CheckConstraint(
            "personal_data_erased_at IS NULL OR "
            "(source_ip IS NULL AND user_agent IS NULL AND request_id IS NULL "
            "AND personal_data_erased_at >= accepted_at)",
            name="personal_data_erasure_valid",
        ),
        _TABLE_ARGS,
    )

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    # The user row becomes an anonymized tombstone on erasure, so this remains a pseudonymous
    # contract-party reference without retaining an email/name snapshot in the evidence table.
    user_id: Mapped[int] = mapped_column(
        Integer,
        ForeignKey("users.id", ondelete="RESTRICT"),
        nullable=False,
    )
    bundle_version: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    terms_version: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    eula_version: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    acceptable_use_version: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    privacy_version: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    terms_sha256: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    eula_sha256: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    acceptable_use_sha256: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    privacy_sha256: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"))
    accepted_at: Mapped[datetime] = mapped_column(DateTime(timezone=False))
    retention_until: Mapped[datetime] = mapped_column(DateTime(timezone=False))
    source_ip: Mapped[str | None] = mapped_column(String(45))
    user_agent: Mapped[str | None] = mapped_column(String(400))
    request_id: Mapped[str | None] = mapped_column(String(64))
    personal_data_erased_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=False))


@sqlalchemy_event.listens_for(LegalAcceptance, "before_update")
@sqlalchemy_event.listens_for(LegalAcceptance, "before_delete")
def _prevent_orm_rewrite(*_args) -> None:
    raise TypeError(
        "LegalAcceptance evidence is append-only; use governed privacy lifecycle operations"
    )
