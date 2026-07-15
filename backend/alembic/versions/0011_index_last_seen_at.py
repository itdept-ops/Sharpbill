"""Index users.last_seen_at (online-count and presence filters)

Revision ID: 0011
Revises: 0010
Create Date: 2026-07-15

The dashboard, analytics, presence, and the online directory filter all compare
last_seen_at against a cutoff on every load; without an index this is a full table scan.

"""

from collections.abc import Sequence

from alembic import op

revision: str = "0011"
down_revision: str | None = "0010"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_index("ix_users_last_seen_at", "users", ["last_seen_at"])


def downgrade() -> None:
    op.drop_index("ix_users_last_seen_at", table_name="users")
