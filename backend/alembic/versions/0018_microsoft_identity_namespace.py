"""Key Microsoft identities by signed tenant plus object ID.

Revision ID: 0018
Revises: 0017
Create Date: 2026-07-20

Google ``sub`` values are globally scoped to Google's issuer. Microsoft ``oid`` values are scoped
to one Entra tenant, so unrestricted provider onboarding requires ``(tid, oid)`` association.
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "0018"
down_revision: str | None = "0017"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def _assert_downgrade_identity_keys_are_unique() -> None:
    duplicate = (
        op.get_bind()
        .execute(
            sa.text(
                "SELECT provider, provider_subject FROM user_identities "
                "GROUP BY provider, provider_subject HAVING COUNT(*) > 1 LIMIT 1"
            )
        )
        .first()
    )
    if duplicate is not None:
        raise RuntimeError(
            "0018 downgrade refused: tenant-scoped Microsoft identities share an oid; "
            "downgrading would collapse distinct accounts"
        )


def upgrade() -> None:
    op.add_column(
        "user_identities",
        sa.Column(
            "provider_namespace",
            sa.String(255, collation="utf8mb4_0900_bin"),
            server_default="",
            nullable=False,
        ),
    )
    op.execute(
        sa.text(
            "UPDATE user_identities SET provider_namespace = provider_tenant_id "
            "WHERE provider = 'microsoft' AND provider_tenant_id IS NOT NULL"
        )
    )
    # A legacy Microsoft row without a signed tid cannot be associated safely. Give it a unique
    # non-authoritative namespace so a future verified login cannot silently claim it.
    op.execute(
        sa.text(
            "UPDATE user_identities SET provider_namespace = CONCAT('legacy:', id) "
            "WHERE provider = 'microsoft' AND provider_tenant_id IS NULL"
        )
    )
    op.drop_constraint("uq_user_identities_provider_subject", "user_identities", type_="unique")
    op.create_unique_constraint(
        "uq_user_identities_provider_namespace_subject",
        "user_identities",
        ["provider", "provider_namespace", "provider_subject"],
    )


def downgrade() -> None:
    _assert_downgrade_identity_keys_are_unique()
    op.drop_constraint(
        "uq_user_identities_provider_namespace_subject", "user_identities", type_="unique"
    )
    op.create_unique_constraint(
        "uq_user_identities_provider_subject",
        "user_identities",
        ["provider", "provider_subject"],
    )
    op.drop_column("user_identities", "provider_namespace")
