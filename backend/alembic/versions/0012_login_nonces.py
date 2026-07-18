"""Single-use OIDC login nonces (login_nonces table)

Revision ID: 0012
Revises: 0011
Create Date: 2026-07-15

Backs the OIDC nonce (and cross-process id_token replay defense): a nonce is issued before a
provider sign-in, echoed in the id_token, and consumed here exactly once.

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0012"
down_revision: str | None = "0011"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_TABLE_ARGS = dict(
    mysql_engine="InnoDB",
    mysql_charset="utf8mb4",
    mysql_collate="utf8mb4_0900_ai_ci",
)


def upgrade() -> None:
    op.create_table(
        "login_nonces",
        sa.Column("nonce", sa.String(64), nullable=False),
        sa.Column(
            "created_at",
            mysql.DATETIME(fsp=6),
            nullable=False,
            server_default=sa.text("CURRENT_TIMESTAMP(6)"),
        ),
        sa.Column("expires_at", mysql.DATETIME(fsp=6), nullable=False),
        sa.PrimaryKeyConstraint("nonce", name="pk_login_nonces"),
        **_TABLE_ARGS,
    )
    op.create_index("ix_login_nonces_expires_at", "login_nonces", ["expires_at"])


def downgrade() -> None:
    op.drop_table("login_nonces")
