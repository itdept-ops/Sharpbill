"""Add privacy lifecycle state, retention hold, and privacy administration.

Revision ID: 0019
Revises: 0018
Create Date: 2026-07-20

The identity provider email was a mutable duplicate of ``users.email`` and was never part of
identity association. Removing it minimizes retained PII; downgrade recreates an empty nullable
column because discarded personal data is intentionally not recoverable.
"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0019"
down_revision: str | None = "0018"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_PRIVACY_PERMISSION_KEY = "privacy.manage"
_PRIVACY_PERMISSION_DESCRIPTION = "Manage privacy requests, retention, and legal holds"


def _assert_upgrade_preconditions() -> None:
    bind = op.get_bind()
    existing_permission = bind.execute(
        sa.text("SELECT id FROM permissions WHERE `key` = :key LIMIT 1"),
        {"key": _PRIVACY_PERMISSION_KEY},
    ).first()
    if existing_permission is not None:
        raise RuntimeError(
            "0019 cannot reserve built-in permission key 'privacy.manage'; "
            "rename the existing custom permission before retrying"
        )

    admin_role = bind.execute(
        sa.text("SELECT id, is_system FROM roles WHERE name = 'admin' LIMIT 1")
    ).first()
    if admin_role is None or not bool(admin_role.is_system):
        raise RuntimeError(
            "0019 requires the built-in system admin role before privacy.manage can be seeded"
        )


def _assert_downgrade_preconditions() -> None:
    bind = op.get_bind()
    held = bind.execute(
        sa.text(
            "SELECT id FROM site_settings "
            "WHERE retention_hold <> 0 OR retention_hold_reference IS NOT NULL LIMIT 1"
        )
    ).first()
    if held is not None:
        raise RuntimeError(
            "0019 downgrade refused: release the retention hold and clear its reference first"
        )

    lifecycle_data = bind.execute(
        sa.text(
            "SELECT id FROM users WHERE deactivated_at IS NOT NULL "
            "OR erasure_requested_at IS NOT NULL OR erasure_due_at IS NOT NULL "
            "OR erased_at IS NOT NULL LIMIT 1"
        )
    ).first()
    if lifecycle_data is not None:
        raise RuntimeError(
            "0019 downgrade refused: privacy lifecycle evidence would be lost; "
            "clear the lifecycle timestamps explicitly before retrying"
        )

    permission = bind.execute(
        sa.text("SELECT id, description, is_system FROM permissions WHERE `key` = :key LIMIT 1"),
        {"key": _PRIVACY_PERMISSION_KEY},
    ).first()
    if (
        permission is None
        or not bool(permission.is_system)
        or permission.description != _PRIVACY_PERMISSION_DESCRIPTION
    ):
        raise RuntimeError(
            "0019 downgrade refused: the built-in privacy.manage permission is missing or modified"
        )

    non_admin_grant = bind.execute(
        sa.text(
            "SELECT r.name FROM role_permissions rp "
            "JOIN roles r ON r.id = rp.role_id "
            "WHERE rp.permission_id = :permission_id AND r.name <> 'admin' LIMIT 1"
        ),
        {"permission_id": permission.id},
    ).first()
    direct_grant = bind.execute(
        sa.text(
            "SELECT user_id FROM user_permissions WHERE permission_id = :permission_id LIMIT 1"
        ),
        {"permission_id": permission.id},
    ).first()
    if non_admin_grant is not None or direct_grant is not None:
        raise RuntimeError(
            "0019 downgrade refused: privacy.manage has retained non-admin grants; "
            "remove those grants explicitly before retrying"
        )


def upgrade() -> None:
    _assert_upgrade_preconditions()

    op.add_column("users", sa.Column("deactivated_at", mysql.DATETIME(fsp=6), nullable=True))
    op.add_column("users", sa.Column("erasure_requested_at", mysql.DATETIME(fsp=6), nullable=True))
    op.add_column("users", sa.Column("erasure_due_at", mysql.DATETIME(fsp=6), nullable=True))
    op.add_column("users", sa.Column("erased_at", mysql.DATETIME(fsp=6), nullable=True))

    # The historical time of deactivation is unknowable. Start legacy inactive accounts at the
    # migration time so no account is erased immediately based on a guessed timestamp.
    op.execute(
        sa.text(
            "UPDATE users SET deactivated_at = CURRENT_TIMESTAMP(6) "
            "WHERE is_active = 0 AND deactivated_at IS NULL"
        )
    )

    op.create_check_constraint(
        op.f("ck_users_deactivation_state_valid"),
        "users",
        "(is_active = 1 AND deactivated_at IS NULL) OR "
        "(is_active = 0 AND deactivated_at IS NOT NULL)",
    )
    op.create_check_constraint(
        op.f("ck_users_erasure_schedule_valid"),
        "users",
        "(erasure_requested_at IS NULL AND erasure_due_at IS NULL) OR "
        "(erasure_requested_at IS NOT NULL AND erasure_due_at IS NOT NULL "
        "AND erasure_due_at >= erasure_requested_at)",
    )
    op.create_check_constraint(
        op.f("ck_users_erasure_state_valid"),
        "users",
        "erased_at IS NULL OR "
        "(is_active = 0 AND is_approved = 0 AND deactivated_at IS NOT NULL "
        "AND erased_at >= deactivated_at "
        "AND (erasure_requested_at IS NULL OR erased_at >= erasure_requested_at))",
    )
    op.create_index("ix_users_last_location_at_id", "users", ["last_location_at", "id"])
    op.create_index("ix_users_deactivated_at_id", "users", ["deactivated_at", "id"])
    op.create_index("ix_users_erasure_due_at_id", "users", ["erasure_due_at", "id"])

    op.add_column(
        "site_settings",
        sa.Column(
            "retention_hold",
            mysql.TINYINT(display_width=1),
            nullable=False,
            server_default=sa.text("0"),
        ),
    )
    op.add_column(
        "site_settings", sa.Column("retention_hold_reference", sa.String(255), nullable=True)
    )
    op.create_check_constraint(
        op.f("ck_site_settings_retention_hold_boolean"),
        "site_settings",
        "retention_hold IN (0, 1)",
    )
    op.create_check_constraint(
        op.f("ck_site_settings_retention_hold_state_valid"),
        "site_settings",
        "(retention_hold = 0 AND retention_hold_reference IS NULL) OR "
        "(retention_hold = 1 AND retention_hold_reference IS NOT NULL "
        "AND CHAR_LENGTH(TRIM(retention_hold_reference)) BETWEEN 1 AND 255)",
    )

    op.drop_column("user_identities", "provider_email")

    permissions = sa.table(
        "permissions",
        sa.column("key", sa.String()),
        sa.column("description", sa.String()),
        sa.column("is_system", sa.Boolean()),
    )
    op.bulk_insert(
        permissions,
        [
            {
                "key": _PRIVACY_PERMISSION_KEY,
                "description": _PRIVACY_PERMISSION_DESCRIPTION,
                "is_system": True,
            }
        ],
    )
    op.execute(
        sa.text(
            "INSERT INTO role_permissions (role_id, permission_id) "
            "SELECT r.id, p.id FROM roles r JOIN permissions p "
            "WHERE r.name = 'admin' AND p.`key` = :key"
        ).bindparams(key=_PRIVACY_PERMISSION_KEY)
    )


def downgrade() -> None:
    _assert_downgrade_preconditions()

    op.execute(
        sa.text(
            "DELETE rp FROM role_permissions rp "
            "JOIN roles r ON r.id = rp.role_id "
            "JOIN permissions p ON p.id = rp.permission_id "
            "WHERE r.name = 'admin' AND p.`key` = :key"
        ).bindparams(key=_PRIVACY_PERMISSION_KEY)
    )
    op.execute(
        sa.text("DELETE FROM permissions WHERE `key` = :key AND is_system = 1").bindparams(
            key=_PRIVACY_PERMISSION_KEY
        )
    )

    op.add_column("user_identities", sa.Column("provider_email", sa.String(255), nullable=True))

    op.drop_constraint(
        op.f("ck_site_settings_retention_hold_state_valid"),
        "site_settings",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_site_settings_retention_hold_boolean"),
        "site_settings",
        type_="check",
    )
    op.drop_column("site_settings", "retention_hold_reference")
    op.drop_column("site_settings", "retention_hold")

    op.drop_index("ix_users_erasure_due_at_id", table_name="users")
    op.drop_index("ix_users_deactivated_at_id", table_name="users")
    op.drop_index("ix_users_last_location_at_id", table_name="users")
    op.drop_constraint(op.f("ck_users_erasure_state_valid"), "users", type_="check")
    op.drop_constraint(op.f("ck_users_erasure_schedule_valid"), "users", type_="check")
    op.drop_constraint(op.f("ck_users_deactivation_state_valid"), "users", type_="check")
    op.drop_column("users", "erased_at")
    op.drop_column("users", "erasure_due_at")
    op.drop_column("users", "erasure_requested_at")
    op.drop_column("users", "deactivated_at")
