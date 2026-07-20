"""Persist signed identity-provider organization authority.

Revision ID: 0017
Revises: 0016
Create Date: 2026-07-20

The new values are deliberately nullable: legacy identities have no trustworthy way to recover
the signed Google ``hd`` or Microsoft ``tid`` claim. They become populated on the next successful
provider login. Administrative readiness treats a missing claim as unknown and fails closed when
the corresponding organization allowlist is enabled.
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "0017"
down_revision: str | None = "0016"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def _assert_no_admission_authority_would_be_lost() -> None:
    identity = (
        op.get_bind()
        .execute(
            sa.text(
                "SELECT id FROM user_identities "
                "WHERE provider_tenant_id IS NOT NULL "
                "OR provider_hosted_domain IS NOT NULL LIMIT 1"
            )
        )
        .first()
    )
    if identity is not None:
        raise RuntimeError(
            "0017 downgrade refused: persisted provider admission authority would be lost; "
            "explicitly clear the authority columns before retrying"
        )


def upgrade() -> None:
    op.add_column("user_identities", sa.Column("provider_tenant_id", sa.String(255)))
    op.add_column("user_identities", sa.Column("provider_hosted_domain", sa.String(255)))


def downgrade() -> None:
    _assert_no_admission_authority_would_be_lost()
    op.drop_column("user_identities", "provider_hosted_domain")
    op.drop_column("user_identities", "provider_tenant_id")
