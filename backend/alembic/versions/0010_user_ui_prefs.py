"""Per-user UI preferences bag (users.ui_prefs JSON)

Revision ID: 0010
Revises: 0009
Create Date: 2026-07-15

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0010"
down_revision: str | None = "0009"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    # One extensible JSON bag for all per-user UI axes (theme, glow, motion, rain, density,
    # typography, accessibility). NULL = "all defaults" so existing rows render unchanged.
    # MySQL JSON columns cannot carry a non-NULL server default, so the column is nullable.
    op.add_column("users", sa.Column("ui_prefs", mysql.JSON(), nullable=True))


def downgrade() -> None:
    op.drop_column("users", "ui_prefs")
