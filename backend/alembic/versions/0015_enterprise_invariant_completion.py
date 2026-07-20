"""Complete lifecycle invariants and bounded-cleanup indexes.

Revision ID: 0015
Revises: 0014
Create Date: 2026-07-20

MySQL DDL auto-commits. Every data-dependent refusal therefore runs before the first DDL so a
failed preflight leaves both schema and recorded Alembic revision unchanged.
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "0015"
down_revision: str | None = "0014"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_BOOLEAN_COLUMNS = (
    ("users", "is_active"),
    ("users", "is_approved"),
    ("roles", "is_system"),
    ("permissions", "is_system"),
)


def _assert_boolean_values_are_valid() -> None:
    bind = op.get_bind()
    for table, column in _BOOLEAN_COLUMNS:
        invalid = bind.execute(
            sa.text(f"SELECT id, {column} FROM {table} WHERE {column} NOT IN (0, 1) LIMIT 1")
        ).first()
        if invalid is not None:
            raise RuntimeError(
                f"0015 cannot constrain {table}.{column} while an invalid row exists: "
                f"{tuple(invalid)}"
            )


def _assert_default_role_is_not_admin() -> None:
    invalid = (
        op.get_bind()
        .execute(
            sa.text(
                "SELECT s.id FROM site_settings s "
                "JOIN roles r ON r.id = s.default_role_id "
                "WHERE r.name = 'admin' LIMIT 1"
            )
        )
        .first()
    )
    if invalid is not None:
        raise RuntimeError(
            "0015 refused: the site signup default is the protected admin role; select a "
            "non-admin default role before retrying"
        )


def upgrade() -> None:
    _assert_boolean_values_are_valid()
    _assert_default_role_is_not_admin()

    op.create_check_constraint(op.f("ck_users_is_active_boolean"), "users", "is_active IN (0, 1)")
    op.add_column(
        "roles", sa.Column("version", sa.Integer(), server_default=sa.text("1"), nullable=False)
    )
    op.add_column(
        "users",
        sa.Column("access_version", sa.Integer(), server_default=sa.text("1"), nullable=False),
    )
    op.create_check_constraint(
        op.f("ck_users_is_approved_boolean"), "users", "is_approved IN (0, 1)"
    )
    op.create_check_constraint(op.f("ck_roles_is_system_boolean"), "roles", "is_system IN (0, 1)")
    op.create_check_constraint(
        op.f("ck_permissions_is_system_boolean"), "permissions", "is_system IN (0, 1)"
    )
    op.create_index("ix_user_sessions_revoked_at", "user_sessions", ["revoked_at"])

    # Keep the operator-facing RBAC catalog synchronized by stable natural key.
    op.execute(
        sa.text(
            "UPDATE permissions SET description = 'Manage user profiles, activation, and approval' "
            "WHERE `key` = 'users.manage'"
        )
    )
    op.execute(
        sa.text(
            "UPDATE permissions SET description = 'Manage site-wide configuration' "
            "WHERE `key` = 'settings.manage'"
        )
    )


def downgrade() -> None:
    op.execute(
        sa.text(
            "UPDATE permissions SET description = 'Change user roles and activation status' "
            "WHERE `key` = 'users.manage'"
        )
    )
    op.execute(
        sa.text(
            "UPDATE permissions SET description = 'Manage site settings and approve sign-ups' "
            "WHERE `key` = 'settings.manage'"
        )
    )
    op.drop_index("ix_user_sessions_revoked_at", table_name="user_sessions")
    op.drop_constraint(op.f("ck_permissions_is_system_boolean"), "permissions", type_="check")
    op.drop_constraint(op.f("ck_roles_is_system_boolean"), "roles", type_="check")
    op.drop_constraint(op.f("ck_users_is_approved_boolean"), "users", type_="check")
    op.drop_constraint(op.f("ck_users_is_active_boolean"), "users", type_="check")
    op.drop_column("users", "access_version")
    op.drop_column("roles", "version")
