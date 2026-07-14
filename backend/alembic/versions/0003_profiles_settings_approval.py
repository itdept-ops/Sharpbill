"""Profiles, site settings, signup approval, settings.manage permission

Adds user profile columns + is_approved, the site_settings singleton, and the
settings.manage permission (attached to the admin role).

Revision ID: 0003
Revises: 0002
Create Date: 2026-07-14

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0003"
down_revision: str | None = "0002"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_TABLE_ARGS = dict(
    mysql_engine="InnoDB",
    mysql_charset="utf8mb4",
    mysql_collate="utf8mb4_0900_ai_ci",
)


def upgrade() -> None:
    # ---- new permission: settings.manage (id 6), granted to the admin role (id 1) ----
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
                "id": 6,
                "key": "settings.manage",
                "description": "Manage site settings and approve sign-ups",
                "is_system": 1,
            }
        ],
    )
    rp_t = sa.table(
        "role_permissions", sa.column("role_id", sa.Integer), sa.column("permission_id", sa.Integer)
    )
    op.bulk_insert(rp_t, [{"role_id": 1, "permission_id": 6}])

    # ---- user profile columns + approval flag ----
    op.add_column("users", sa.Column("title", sa.String(120), nullable=True))
    op.add_column("users", sa.Column("department", sa.String(120), nullable=True))
    op.add_column("users", sa.Column("phone", sa.String(40), nullable=True))
    op.add_column("users", sa.Column("location", sa.String(120), nullable=True))
    op.add_column("users", sa.Column("timezone", sa.String(60), nullable=True))
    op.add_column("users", sa.Column("bio", sa.String(500), nullable=True))
    op.add_column(
        "users",
        sa.Column(
            "is_approved",
            mysql.TINYINT(display_width=1),
            nullable=False,
            server_default=sa.text("1"),
        ),
    )

    # ---- site settings singleton ----
    op.create_table(
        "site_settings",
        sa.Column("id", sa.Integer(), nullable=False),
        sa.Column("signup_mode", sa.String(20), nullable=False, server_default="open"),
        sa.Column(
            "allow_google",
            mysql.TINYINT(display_width=1),
            nullable=False,
            server_default=sa.text("1"),
        ),
        sa.Column(
            "allow_microsoft",
            mysql.TINYINT(display_width=1),
            nullable=False,
            server_default=sa.text("1"),
        ),
        sa.Column("default_role_id", sa.Integer(), nullable=False),
        sa.Column(
            "updated_at",
            mysql.DATETIME(fsp=6),
            nullable=False,
            server_default=sa.text("CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)"),
        ),
        sa.PrimaryKeyConstraint("id", name="pk_site_settings"),
        sa.ForeignKeyConstraint(
            ["default_role_id"],
            ["roles.id"],
            name="fk_site_settings_default_role_id_roles",
            ondelete="RESTRICT",
        ),
        **_TABLE_ARGS,
    )
    settings_t = sa.table(
        "site_settings",
        sa.column("id", sa.Integer),
        sa.column("signup_mode", sa.String),
        sa.column("allow_google", sa.Integer),
        sa.column("allow_microsoft", sa.Integer),
        sa.column("default_role_id", sa.Integer),
    )
    op.bulk_insert(
        settings_t,
        [
            {
                "id": 1,
                "signup_mode": "open",
                "allow_google": 1,
                "allow_microsoft": 1,
                "default_role_id": 2,
            }
        ],
    )


def downgrade() -> None:
    op.drop_table("site_settings")
    for col in ("is_approved", "bio", "timezone", "location", "phone", "department", "title"):
        op.drop_column("users", col)
    op.execute("DELETE FROM role_permissions WHERE permission_id = 6")
    op.execute("DELETE FROM permissions WHERE id = 6")
