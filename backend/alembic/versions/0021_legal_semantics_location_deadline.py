"""Snapshot legal semantics and add per-capture precise-location expiry.

Revision ID: 0021
Revises: 0020
Create Date: 2026-07-20

The v1/v2 legal artifacts share one known effective date, acceptance label, and action map; each
version has a distinct known digest set. Upgrade refuses unknown or modified evidence before DDL,
so the backfill
never invents semantics. Existing timestamped precise captures receive the historical 24-hour
deadline that was in force before capture-time deadlines were stored explicitly.
"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0021"
down_revision: str | None = "0020"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_BUNDLE_V1 = "2026-07-20-v1"
_BUNDLE_V2 = "2026-07-20-v2"
_EFFECTIVE_DATE = "2026-07-20"
_ACCEPTANCE_LABEL = (
    "I agree to the Terms of Service, EULA, and Acceptable Use Policy, and acknowledge "
    "the Privacy Notice."
)
_V1_TERMS_SHA256 = "2c77250037d037141e79fd11f1a85cde1e9257d51cb325e7fdaefa6cf4f0ff2e"
_V1_EULA_SHA256 = "16bd045a449990e3f7325f0d67d81d4fee54f679ec53164835f0c19725e25638"
_V1_AUP_SHA256 = "d4391a0abe57885964606521039a4cca0151f8e11d95c628efc51b603eefdb0d"
_V1_PRIVACY_SHA256 = "fb96f77cc9846282c9555105994d0dc9b400c2a6eaf35e15b390b5a3c5db2d3d"
_V2_TERMS_SHA256 = "f5a30fded3b6b4715f13d0711c9168dd643aac48ff14164e95bc7610734fb912"
_V2_EULA_SHA256 = "2715b0daa99c2a553b08448eb81307affcfd2ca5ece005563eb4ad83d7fae6b3"
_V2_AUP_SHA256 = "1290bb3dbcf3b79fb2051693ae7be6898b421daf24af1ddb037098cc1ee07217"
_V2_PRIVACY_SHA256 = "53e22a3bff270fb2215631f061cd001f89a96971e6fa3bb8374ff2f829931695"


def _assert_upgrade_preconditions() -> None:
    """Refuse to attach known-v1 semantics to an unrecognized evidence artifact."""
    unknown = (
        op.get_bind()
        .execute(
            sa.text(
                "SELECT id FROM legal_acceptances WHERE NOT ("
                "((bundle_version = :v1 AND terms_version = :v1 AND eula_version = :v1 "
                "AND acceptable_use_version = :v1 AND privacy_version = :v1 "
                "AND terms_sha256 = :v1_terms_sha256 AND eula_sha256 = :v1_eula_sha256 "
                "AND acceptable_use_sha256 = :v1_aup_sha256 "
                "AND privacy_sha256 = :v1_privacy_sha256) OR "
                "(bundle_version = :v2 AND terms_version = :v2 AND eula_version = :v2 "
                "AND acceptable_use_version = :v2 AND privacy_version = :v2 "
                "AND terms_sha256 = :v2_terms_sha256 AND eula_sha256 = :v2_eula_sha256 "
                "AND acceptable_use_sha256 = :v2_aup_sha256 "
                "AND privacy_sha256 = :v2_privacy_sha256)) "
                "AND accepted_at >= :effective_date) LIMIT 1"
            ),
            {
                "v1": _BUNDLE_V1,
                "v2": _BUNDLE_V2,
                "v1_terms_sha256": _V1_TERMS_SHA256,
                "v1_eula_sha256": _V1_EULA_SHA256,
                "v1_aup_sha256": _V1_AUP_SHA256,
                "v1_privacy_sha256": _V1_PRIVACY_SHA256,
                "v2_terms_sha256": _V2_TERMS_SHA256,
                "v2_eula_sha256": _V2_EULA_SHA256,
                "v2_aup_sha256": _V2_AUP_SHA256,
                "v2_privacy_sha256": _V2_PRIVACY_SHA256,
                "effective_date": _EFFECTIVE_DATE,
            },
        )
        .first()
    )
    if unknown is not None:
        raise RuntimeError(
            "0021 cannot safely backfill legal semantics for unknown or modified evidence"
        )
    invalid_location = (
        op.get_bind()
        .execute(
            sa.text(
                "SELECT id FROM users WHERE NOT ("
                "(last_latitude IS NULL AND last_longitude IS NULL "
                "AND last_location_accuracy IS NULL AND last_location_at IS NULL) OR "
                "((last_latitude IS NOT NULL OR last_longitude IS NOT NULL "
                "OR last_location_accuracy IS NOT NULL) AND last_location_at IS NOT NULL)"
                ") LIMIT 1"
            )
        )
        .first()
    )
    if invalid_location is not None:
        raise RuntimeError(
            "0021 cannot backfill a precise-location deadline without a coherent capture time"
        )


def _assert_downgrade_preconditions() -> None:
    bind = op.get_bind()
    if bind.execute(sa.text("SELECT id FROM legal_acceptances LIMIT 1")).first() is not None:
        raise RuntimeError(
            "0021 downgrade refused: legal action/effective-date evidence would be lost"
        )
    if (
        bind.execute(
            sa.text("SELECT id FROM users WHERE location_retention_until IS NOT NULL LIMIT 1")
        ).first()
        is not None
    ):
        raise RuntimeError(
            "0021 downgrade refused: per-capture location retention deadlines would be lost"
        )


def upgrade() -> None:
    _assert_upgrade_preconditions()

    op.add_column("legal_acceptances", sa.Column("bundle_effective_date", sa.Date(), nullable=True))
    op.add_column(
        "legal_acceptances",
        sa.Column(
            "acceptance_label",
            sa.String(500, collation="utf8mb4_0900_bin"),
            nullable=True,
        ),
    )
    for column_name in (
        "terms_action",
        "eula_action",
        "acceptable_use_action",
        "privacy_action",
    ):
        op.add_column(
            "legal_acceptances",
            sa.Column(
                column_name,
                sa.String(16, collation="utf8mb4_0900_bin"),
                nullable=True,
            ),
        )
    op.execute(
        sa.text(
            "UPDATE legal_acceptances SET bundle_effective_date=:effective_date, "
            "acceptance_label=:acceptance_label, terms_action='agreement', "
            "eula_action='agreement', acceptable_use_action='agreement', "
            "privacy_action='acknowledgement'"
        ).bindparams(
            effective_date=_EFFECTIVE_DATE,
            acceptance_label=_ACCEPTANCE_LABEL,
        )
    )
    op.alter_column(
        "legal_acceptances",
        "bundle_effective_date",
        existing_type=sa.Date(),
        nullable=False,
    )
    op.alter_column(
        "legal_acceptances",
        "acceptance_label",
        existing_type=sa.String(500, collation="utf8mb4_0900_bin"),
        nullable=False,
    )
    for column_name in (
        "terms_action",
        "eula_action",
        "acceptable_use_action",
        "privacy_action",
    ):
        op.alter_column(
            "legal_acceptances",
            column_name,
            existing_type=sa.String(16, collation="utf8mb4_0900_bin"),
            nullable=False,
        )

    op.create_check_constraint(
        op.f("ck_legal_acceptances_acceptance_label_valid"),
        "legal_acceptances",
        "CHAR_LENGTH(TRIM(acceptance_label)) BETWEEN 1 AND 500",
    )
    for column_name in (
        "terms_action",
        "eula_action",
        "acceptable_use_action",
        "privacy_action",
    ):
        op.create_check_constraint(
            op.f(f"ck_legal_acceptances_{column_name}_valid"),
            "legal_acceptances",
            f"{column_name} IN ('agreement', 'acknowledgement')",
        )
    op.create_check_constraint(
        op.f("ck_legal_acceptances_effective_date_not_after_acceptance"),
        "legal_acceptances",
        "bundle_effective_date <= DATE(accepted_at)",
    )

    op.add_column(
        "users",
        sa.Column("location_retention_until", mysql.DATETIME(fsp=6), nullable=True),
    )
    op.execute(
        sa.text(
            "UPDATE users SET location_retention_until="
            "DATE_ADD(last_location_at, INTERVAL 24 HOUR) "
            "WHERE last_location_at IS NOT NULL AND "
            "(last_latitude IS NOT NULL OR last_longitude IS NOT NULL "
            "OR last_location_accuracy IS NOT NULL)"
        )
    )
    op.create_check_constraint(
        op.f("ck_users_location_retention_valid"),
        "users",
        "(last_latitude IS NULL AND last_longitude IS NULL "
        "AND last_location_accuracy IS NULL AND last_location_at IS NULL "
        "AND location_retention_until IS NULL) OR "
        "((last_latitude IS NOT NULL OR last_longitude IS NOT NULL "
        "OR last_location_accuracy IS NOT NULL) AND last_location_at IS NOT NULL "
        "AND location_retention_until IS NOT NULL "
        "AND location_retention_until >= last_location_at "
        ")",
    )
    op.create_index(
        "ix_users_location_retention_until_id",
        "users",
        ["location_retention_until", "id"],
    )


def downgrade() -> None:
    _assert_downgrade_preconditions()

    op.drop_index("ix_users_location_retention_until_id", table_name="users")
    op.drop_constraint(op.f("ck_users_location_retention_valid"), "users", type_="check")
    op.drop_column("users", "location_retention_until")

    op.drop_constraint(
        op.f("ck_legal_acceptances_effective_date_not_after_acceptance"),
        "legal_acceptances",
        type_="check",
    )
    for column_name in (
        "privacy_action",
        "acceptable_use_action",
        "eula_action",
        "terms_action",
    ):
        op.drop_constraint(
            op.f(f"ck_legal_acceptances_{column_name}_valid"),
            "legal_acceptances",
            type_="check",
        )
    op.drop_constraint(
        op.f("ck_legal_acceptances_acceptance_label_valid"),
        "legal_acceptances",
        type_="check",
    )
    for column_name in (
        "privacy_action",
        "acceptable_use_action",
        "eula_action",
        "terms_action",
        "acceptance_label",
        "bundle_effective_date",
    ):
        op.drop_column("legal_acceptances", column_name)
