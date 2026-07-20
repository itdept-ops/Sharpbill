"""Add immutable, versioned legal-acceptance evidence.

Revision ID: 0020
Revises: 0019
Create Date: 2026-07-20

Every issued login session is paired with exact document-version and canonical SHA-256 snapshots.
Direct request metadata is nullable so account erasure can minimize PII without discarding
required contract evidence; the row itself expires through the hold-aware retention worker.
"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0020"
down_revision: str | None = "0019"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def _assert_downgrade_preconditions() -> None:
    retained = op.get_bind().execute(sa.text("SELECT id FROM legal_acceptances LIMIT 1")).first()
    if retained is not None:
        raise RuntimeError(
            "0020 downgrade refused: legal acceptance evidence would be lost; "
            "retain the migration or remove the evidence under an approved retention process"
        )


def upgrade() -> None:
    op.create_table(
        "legal_acceptances",
        sa.Column("id", sa.BigInteger(), autoincrement=True, nullable=False),
        sa.Column("user_id", sa.Integer(), nullable=False),
        sa.Column(
            "bundle_version",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column(
            "terms_version",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column(
            "eula_version",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column(
            "acceptable_use_version",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column(
            "privacy_version",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column(
            "terms_sha256",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column(
            "eula_sha256",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column(
            "acceptable_use_sha256",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column(
            "privacy_sha256",
            sa.String(64, collation="utf8mb4_0900_bin"),
            nullable=False,
        ),
        sa.Column("accepted_at", mysql.DATETIME(fsp=6), nullable=False),
        sa.Column("retention_until", mysql.DATETIME(fsp=6), nullable=False),
        sa.Column("source_ip", sa.String(45), nullable=True),
        sa.Column("user_agent", sa.String(400), nullable=True),
        sa.Column("request_id", sa.String(64), nullable=True),
        sa.Column("personal_data_erased_at", mysql.DATETIME(fsp=6), nullable=True),
        sa.CheckConstraint(
            "terms_sha256 REGEXP '^[0-9a-f]{64}$'",
            name=op.f("ck_legal_acceptances_terms_sha256_valid"),
        ),
        sa.CheckConstraint(
            "eula_sha256 REGEXP '^[0-9a-f]{64}$'",
            name=op.f("ck_legal_acceptances_eula_sha256_valid"),
        ),
        sa.CheckConstraint(
            "acceptable_use_sha256 REGEXP '^[0-9a-f]{64}$'",
            name=op.f("ck_legal_acceptances_acceptable_use_sha256_valid"),
        ),
        sa.CheckConstraint(
            "privacy_sha256 REGEXP '^[0-9a-f]{64}$'",
            name=op.f("ck_legal_acceptances_privacy_sha256_valid"),
        ),
        sa.CheckConstraint(
            "retention_until > accepted_at",
            name=op.f("ck_legal_acceptances_retention_after_acceptance"),
        ),
        sa.CheckConstraint(
            "personal_data_erased_at IS NULL OR "
            "(source_ip IS NULL AND user_agent IS NULL AND request_id IS NULL "
            "AND personal_data_erased_at >= accepted_at)",
            name=op.f("ck_legal_acceptances_personal_data_erasure_valid"),
        ),
        sa.ForeignKeyConstraint(
            ["user_id"],
            ["users.id"],
            name=op.f("fk_legal_acceptances_user_id_users"),
            ondelete="RESTRICT",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_legal_acceptances")),
        mysql_engine="InnoDB",
        mysql_charset="utf8mb4",
        mysql_collate="utf8mb4_0900_ai_ci",
    )
    op.create_index(
        "ix_legal_acceptances_user_accepted_id",
        "legal_acceptances",
        ["user_id", "accepted_at", "id"],
    )
    op.create_index(
        "ix_legal_acceptances_accepted_id",
        "legal_acceptances",
        ["accepted_at", "id"],
    )
    op.create_index(
        "ix_legal_acceptances_retention_id",
        "legal_acceptances",
        ["retention_until", "id"],
    )


def downgrade() -> None:
    _assert_downgrade_preconditions()
    # Drop the table as one DDL operation. MySQL may select the explicit user index to enforce
    # the FK, so attempting to drop that index separately can fail midway through a non-
    # transactional downgrade and strand a partially modified 0020 schema.
    op.drop_table("legal_acceptances")
