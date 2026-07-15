"""Per-user permission grants: user_permissions join table (RBAC + direct grants)

Revision ID: 0008
Revises: 0007
Create Date: 2026-07-15

"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "0008"
down_revision: str | None = "0007"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_TABLE_ARGS = dict(
    mysql_engine="InnoDB",
    mysql_charset="utf8mb4",
    mysql_collate="utf8mb4_0900_ai_ci",
)


def upgrade() -> None:
    op.create_table(
        "user_permissions",
        sa.Column("user_id", sa.Integer(), nullable=False),
        sa.Column("permission_id", sa.Integer(), nullable=False),
        sa.PrimaryKeyConstraint("user_id", "permission_id", name="pk_user_permissions"),
        sa.ForeignKeyConstraint(
            ["user_id"], ["users.id"], name="fk_user_permissions_user_id", ondelete="CASCADE"
        ),
        sa.ForeignKeyConstraint(
            ["permission_id"],
            ["permissions.id"],
            name="fk_user_permissions_permission_id",
            ondelete="CASCADE",
        ),
        **_TABLE_ARGS,
    )


def downgrade() -> None:
    op.drop_table("user_permissions")
