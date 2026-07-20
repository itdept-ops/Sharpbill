from datetime import datetime

from sqlalchemy import DateTime, String, text
from sqlalchemy.orm import Mapped, mapped_column

from app.models.base import Base

_TABLE_ARGS = {
    "mysql_engine": "InnoDB",
    "mysql_charset": "utf8mb4",
    "mysql_collate": "utf8mb4_0900_ai_ci",
}


class LoginNonce(Base):
    """A single-use OIDC login nonce, stored in the DB so it is shared across workers/instances.

    Issued (GET /api/auth/nonce) before a provider sign-in and echoed back in the id_token's
    `nonce` claim, which the verifier consumes exactly once. Being DB-backed, a single-use nonce
    also defeats id_token replay across processes (the in-memory guard is only per-worker).
    """

    __tablename__ = "login_nonces"
    __table_args__ = _TABLE_ARGS

    # OIDC nonces are opaque and case-sensitive. The explicit binary collation also governs
    # primary-key lookups, so a case-modified nonce cannot consume the issued value.
    nonce: Mapped[str] = mapped_column(String(64, collation="utf8mb4_0900_bin"), primary_key=True)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=False), server_default=text("CURRENT_TIMESTAMP(6)")
    )
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=False), index=True)
