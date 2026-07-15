"""Global calm-mode site setting + per-user accent color

Revision ID: 0009
Revises: 0008
Create Date: 2026-07-15

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0009"
down_revision: str | None = "0008"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column(
        "site_settings",
        sa.Column("calm_mode", mysql.TINYINT(1), nullable=False, server_default=sa.text("0")),
    )
    op.add_column("users", sa.Column("accent_color", sa.String(9), nullable=True))


def downgrade() -> None:
    op.drop_column("users", "accent_color")
    op.drop_column("site_settings", "calm_mode")
