"""Split sensitive exports and security evidence into least-privilege grants.

Revision ID: 0016
Revises: 0015
Create Date: 2026-07-20
"""

from collections.abc import Sequence

import sqlalchemy as sa

from alembic import op

revision: str = "0016"
down_revision: str | None = "0015"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_PERMISSIONS = (
    ("users.export", "Export the user directory as CSV"),
    ("security_events.view", "View and export durable security events"),
)


def _assert_reserved_keys_are_available() -> None:
    keys = tuple(key for key, _ in _PERMISSIONS)
    existing = (
        op.get_bind()
        .execute(
            sa.text("SELECT `key` FROM permissions WHERE `key` IN :keys LIMIT 1").bindparams(
                sa.bindparam("keys", expanding=True)
            ),
            {"keys": keys},
        )
        .first()
    )
    if existing is not None:
        raise RuntimeError(
            f"0016 cannot reserve built-in permission key {existing[0]!r}; "
            "rename the existing custom permission before retrying"
        )


def _assert_no_non_admin_grants() -> None:
    bind = op.get_bind()
    role_grant = bind.execute(
        sa.text(
            "SELECT p.`key`, r.name FROM role_permissions rp "
            "JOIN permissions p ON p.id = rp.permission_id "
            "JOIN roles r ON r.id = rp.role_id "
            "WHERE p.`key` IN ('users.export', 'security_events.view') "
            "AND r.name <> 'admin' LIMIT 1"
        )
    ).first()
    direct_grant = bind.execute(
        sa.text(
            "SELECT p.`key`, up.user_id FROM user_permissions up "
            "JOIN permissions p ON p.id = up.permission_id "
            "WHERE p.`key` IN ('users.export', 'security_events.view') LIMIT 1"
        )
    ).first()
    if role_grant is not None or direct_grant is not None:
        raise RuntimeError(
            "0016 downgrade refused: least-privilege permissions have retained grants; "
            "remove those grants explicitly before retrying"
        )


def upgrade() -> None:
    _assert_reserved_keys_are_available()
    permissions = sa.table(
        "permissions",
        sa.column("key", sa.String()),
        sa.column("description", sa.String()),
        sa.column("is_system", sa.Boolean()),
    )
    op.bulk_insert(
        permissions,
        [
            {"key": key, "description": description, "is_system": True}
            for key, description in _PERMISSIONS
        ],
    )
    op.execute(
        sa.text(
            "INSERT INTO role_permissions (role_id, permission_id) "
            "SELECT r.id, p.id FROM roles r JOIN permissions p "
            "WHERE r.name = 'admin' "
            "AND p.`key` IN ('users.export', 'security_events.view')"
        )
    )


def downgrade() -> None:
    _assert_no_non_admin_grants()
    op.execute(
        sa.text(
            "DELETE rp FROM role_permissions rp "
            "JOIN roles r ON r.id = rp.role_id "
            "JOIN permissions p ON p.id = rp.permission_id "
            "WHERE r.name = 'admin' "
            "AND p.`key` IN ('users.export', 'security_events.view')"
        )
    )
    op.execute(
        sa.text(
            "DELETE FROM permissions "
            "WHERE `key` IN ('users.export', 'security_events.view') AND is_system = 1"
        )
    )
