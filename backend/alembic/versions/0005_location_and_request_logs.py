"""Optional login GPS on users + request activity log + logs.view permission

Revision ID: 0005
Revises: 0004
Create Date: 2026-07-14

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0005"
down_revision: str | None = "0004"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_TABLE_ARGS = dict(
    mysql_engine="InnoDB",
    mysql_charset="utf8mb4",
    mysql_collate="utf8mb4_0900_ai_ci",
)


def upgrade() -> None:
    # ---- optional last-known location on users ----
    op.add_column("users", sa.Column("last_latitude", mysql.DOUBLE(asdecimal=False), nullable=True))
    op.add_column(
        "users", sa.Column("last_longitude", mysql.DOUBLE(asdecimal=False), nullable=True)
    )
    op.add_column(
        "users", sa.Column("last_location_accuracy", mysql.DOUBLE(asdecimal=False), nullable=True)
    )
    op.add_column("users", sa.Column("last_location_at", mysql.DATETIME(fsp=6), nullable=True))

    # ---- logs.view permission (id 9), granted to admin (role 1) ----
    perms_t = sa.table(
        "permissions",
        sa.column("id", sa.Integer),
        sa.column("key", sa.String),
        sa.column("description", sa.String),
        sa.column("is_system", sa.Integer),
    )
    op.bulk_insert(
        perms_t,
        [
            {
                "id": 9,
                "key": "logs.view",
                "description": "View the request activity log",
                "is_system": 1,
            }
        ],
    )
    rp_t = sa.table(
        "role_permissions", sa.column("role_id", sa.Integer), sa.column("permission_id", sa.Integer)
    )
    op.bulk_insert(rp_t, [{"role_id": 1, "permission_id": 9}])

    # ---- request activity log ----
    op.create_table(
        "request_logs",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("method", sa.String(10), nullable=False),
        sa.Column("path", sa.String(255), nullable=False),
        sa.Column("user_id", sa.Integer(), nullable=True),
        sa.Column("ip", sa.String(45), nullable=True),
        sa.Column("status_code", sa.Integer(), nullable=False),
        sa.Column(
            "created_at",
            mysql.DATETIME(fsp=6),
            nullable=False,
            server_default=sa.text("CURRENT_TIMESTAMP(6)"),
        ),
        sa.PrimaryKeyConstraint("id", name="pk_request_logs"),
        **_TABLE_ARGS,
    )
    op.create_index("ix_request_logs_user_id", "request_logs", ["user_id"])
    op.create_index("ix_request_logs_created_at", "request_logs", ["created_at"])


def downgrade() -> None:
    op.drop_table("request_logs")
    op.execute("DELETE FROM role_permissions WHERE permission_id = 9")
    op.execute("DELETE FROM permissions WHERE id = 9")
    for col in ("last_location_at", "last_location_accuracy", "last_longitude", "last_latitude"):
        op.drop_column("users", col)
