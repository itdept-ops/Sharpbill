"""Remove the Contacts feature: drop the contacts table + contacts.read/write permissions

Revision ID: 0006
Revises: 0005
Create Date: 2026-07-14

The app was refocused on user management; the Contacts CRM (added in 0004) is removed. This
drops the table and deletes permission ids 7/8 and all their role grants. logs.view (id 9) stays.

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0006"
down_revision: str | None = "0005"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_TABLE_ARGS = dict(
    mysql_engine="InnoDB",
    mysql_charset="utf8mb4",
    mysql_collate="utf8mb4_0900_ai_ci",
)


def upgrade() -> None:
    op.drop_table("contacts")
    op.execute("DELETE FROM role_permissions WHERE permission_id IN (7, 8)")
    op.execute("DELETE FROM permissions WHERE id IN (7, 8)")


def downgrade() -> None:
    # Mirror of 0004's upgrade — restore permissions 7/8, their admin(1)/user(2) grants,
    # and the contacts table.
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
            {"id": 7, "key": "contacts.read", "description": "View contacts", "is_system": 1},
            {
                "id": 8,
                "key": "contacts.write",
                "description": "Create, edit, and delete contacts",
                "is_system": 1,
            },
        ],
    )
    rp_t = sa.table(
        "role_permissions", sa.column("role_id", sa.Integer), sa.column("permission_id", sa.Integer)
    )
    op.bulk_insert(
        rp_t,
        [
            {"role_id": 1, "permission_id": 7},
            {"role_id": 1, "permission_id": 8},
            {"role_id": 2, "permission_id": 7},
            {"role_id": 2, "permission_id": 8},
        ],
    )
    op.create_table(
        "contacts",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("first_name", sa.String(120), nullable=False),
        sa.Column("last_name", sa.String(120), nullable=True),
        sa.Column("email", sa.String(255), nullable=True),
        sa.Column("phone", sa.String(40), nullable=True),
        sa.Column("company", sa.String(160), nullable=True),
        sa.Column("title", sa.String(120), nullable=True),
        sa.Column("status", sa.String(20), nullable=False, server_default="lead"),
        sa.Column("owner_id", sa.Integer(), nullable=True),
        sa.Column("notes", sa.String(2000), nullable=True),
        sa.Column(
            "created_at",
            mysql.DATETIME(fsp=6),
            nullable=False,
            server_default=sa.text("CURRENT_TIMESTAMP(6)"),
        ),
        sa.Column(
            "updated_at",
            mysql.DATETIME(fsp=6),
            nullable=False,
            server_default=sa.text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
        ),
        sa.PrimaryKeyConstraint("id", name="pk_contacts"),
        sa.ForeignKeyConstraint(
            ["owner_id"], ["users.id"], name="fk_contacts_owner_id_users", ondelete="SET NULL"
        ),
        **_TABLE_ARGS,
    )
    op.create_index("ix_contacts_email", "contacts", ["email"])
    op.create_index("ix_contacts_status", "contacts", ["status"])
    op.create_index("ix_contacts_owner_id", "contacts", ["owner_id"])
