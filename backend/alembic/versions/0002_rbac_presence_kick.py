"""RBAC (roles + permissions), presence, and session kick

Adds permissions/roles/role_permissions, seeds the built-in permissions and the admin/user
system roles, migrates users.role (string) -> users.role_id (FK), and adds presence
(last_seen_at) and session-kill-switch (session_valid_after) columns.

Revision ID: 0002
Revises: 0001
Create Date: 2026-07-13

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0002"
down_revision: str | None = "0001"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_TABLE_ARGS = dict(
    mysql_engine="InnoDB",
    mysql_charset="utf8mb4",
    mysql_collate="utf8mb4_0900_ai_ci",
)

# Static snapshot of the built-in RBAC seed (keep in sync with app/permissions.py; a test
# asserts they match). Permission ids are fixed by insertion order below.
_PERMISSIONS = [
    (1, "users.read", "View the user directory"),
    (2, "users.manage", "Change user roles and activation status"),
    (3, "roles.manage", "Create and edit roles and permissions"),
    (4, "presence.view", "See who is currently online"),
    (5, "presence.kick", "Force sign-out (kick) a user's active sessions"),
]
_ROLES = [
    (1, "admin", "Full access to every feature."),
    (2, "user", "Standard access for new members."),
]
_ROLE_PERMS = [(1, [1, 2, 3, 4, 5]), (2, [4])]  # admin -> all, user -> presence.view


def upgrade() -> None:
    op.create_table(
        "permissions",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("key", sa.String(100), nullable=False),
        sa.Column("description", sa.String(255), nullable=True),
        sa.Column(
            "is_system", mysql.TINYINT(display_width=1), nullable=False, server_default=sa.text("0")
        ),
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
        sa.PrimaryKeyConstraint("id", name="pk_permissions"),
        sa.UniqueConstraint("key", name="uq_permissions_key"),
        **_TABLE_ARGS,
    )
    op.create_table(
        "roles",
        sa.Column("id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("name", sa.String(50), nullable=False),
        sa.Column("description", sa.String(255), nullable=True),
        sa.Column(
            "is_system", mysql.TINYINT(display_width=1), nullable=False, server_default=sa.text("0")
        ),
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
        sa.PrimaryKeyConstraint("id", name="pk_roles"),
        sa.UniqueConstraint("name", name="uq_roles_name"),
        **_TABLE_ARGS,
    )
    op.create_table(
        "role_permissions",
        sa.Column("role_id", sa.Integer(), nullable=False),
        sa.Column("permission_id", sa.Integer(), nullable=False),
        sa.PrimaryKeyConstraint("role_id", "permission_id", name="pk_role_permissions"),
        sa.ForeignKeyConstraint(
            ["role_id"], ["roles.id"], name="fk_role_permissions_role_id_roles", ondelete="CASCADE"
        ),
        sa.ForeignKeyConstraint(
            ["permission_id"],
            ["permissions.id"],
            name="fk_role_permissions_permission_id_permissions",
            ondelete="CASCADE",
        ),
        **_TABLE_ARGS,
    )

    # ---- seed built-in permissions, roles, and their links ----
    perms_t = sa.table(
        "permissions",
        sa.column("id", sa.Integer),
        sa.column("key", sa.String),
        sa.column("description", sa.String),
        sa.column("is_system", sa.Integer),
    )
    op.bulk_insert(
        perms_t,
        [{"id": i, "key": k, "description": d, "is_system": 1} for (i, k, d) in _PERMISSIONS],
    )
    roles_t = sa.table(
        "roles",
        sa.column("id", sa.Integer),
        sa.column("name", sa.String),
        sa.column("description", sa.String),
        sa.column("is_system", sa.Integer),
    )
    op.bulk_insert(
        roles_t, [{"id": i, "name": n, "description": d, "is_system": 1} for (i, n, d) in _ROLES]
    )
    rp_t = sa.table(
        "role_permissions", sa.column("role_id", sa.Integer), sa.column("permission_id", sa.Integer)
    )
    op.bulk_insert(
        rp_t,
        [{"role_id": rid, "permission_id": pid} for (rid, pids) in _ROLE_PERMS for pid in pids],
    )

    # ---- new user columns ----
    op.add_column("users", sa.Column("role_id", sa.Integer(), nullable=True))
    op.add_column("users", sa.Column("last_seen_at", mysql.DATETIME(fsp=6), nullable=True))
    op.add_column("users", sa.Column("session_valid_after", mysql.DATETIME(fsp=6), nullable=True))

    # ---- backfill role_id from the old string column, then enforce NOT NULL + FK ----
    op.execute("UPDATE users SET role_id = 1 WHERE role = 'admin'")
    op.execute("UPDATE users SET role_id = 2 WHERE role_id IS NULL")
    op.alter_column("users", "role_id", existing_type=sa.Integer(), nullable=False)
    op.create_index("ix_users_role_id", "users", ["role_id"])
    op.create_foreign_key(
        "fk_users_role_id_roles", "users", "roles", ["role_id"], ["id"], ondelete="RESTRICT"
    )

    op.drop_column("users", "role")


def downgrade() -> None:
    op.add_column("users", sa.Column("role", sa.String(20), nullable=False, server_default="user"))
    # The pre-RBAC string column only ever held 'admin'/'user'. Custom roles (up to 50 chars) do
    # not round-trip into this String(20) — collapse anything that isn't a system role to 'user'
    # so the downgrade can't truncate or fail on a long custom role name.
    op.execute(
        "UPDATE users u JOIN roles r ON u.role_id = r.id "
        "SET u.role = CASE WHEN r.name IN ('admin', 'user') THEN r.name ELSE 'user' END"
    )
    op.drop_constraint("fk_users_role_id_roles", "users", type_="foreignkey")
    op.drop_index("ix_users_role_id", table_name="users")
    op.drop_column("users", "session_valid_after")
    op.drop_column("users", "last_seen_at")
    op.drop_column("users", "role_id")
    op.drop_table("role_permissions")
    op.drop_table("roles")
    op.drop_table("permissions")
