"""Enterprise database integrity, lifecycle, and scale hardening.

Revision ID: 0013
Revises: 0012
Create Date: 2026-07-20

The legacy session rows predate persisted token expiry, so their exact JWT `exp` value is not
recoverable. They are conservatively backfilled from `created_at` using the historical default
eight-hour session lifetime. New ORM rows use the configured session TTL.

"""

from collections.abc import Sequence

import sqlalchemy as sa
from sqlalchemy.dialects import mysql

from alembic import op

revision: str = "0013"
down_revision: str | None = "0012"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_BINARY_COLLATION = "utf8mb4_0900_bin"
_LEGACY_COLLATION = "utf8mb4_0900_ai_ci"


def _assert_site_settings_are_valid() -> None:
    bind = op.get_bind()
    invalid = bind.execute(
        sa.text(
            "SELECT id, signup_mode, allow_google, allow_microsoft, calm_mode "
            "FROM site_settings "
            "WHERE id <> 1 "
            "OR signup_mode NOT IN ('open', 'approval', 'closed') "
            "OR NOT (allow_google = 1 OR allow_microsoft = 1) "
            "OR allow_google NOT IN (0, 1) "
            "OR allow_microsoft NOT IN (0, 1) "
            "OR calm_mode NOT IN (0, 1) "
            "LIMIT 1"
        )
    ).first()
    if invalid is not None:
        raise RuntimeError(
            "0013 cannot add site_settings CHECK constraints while an invalid row exists: "
            f"{tuple(invalid)}"
        )


def _assert_user_locations_are_valid() -> None:
    bind = op.get_bind()
    invalid = bind.execute(
        sa.text(
            "SELECT id, last_latitude, last_longitude, last_location_accuracy "
            "FROM users "
            "WHERE (last_latitude IS NOT NULL "
            "AND NOT (last_latitude BETWEEN -90 AND 90)) "
            "OR (last_longitude IS NOT NULL "
            "AND NOT (last_longitude BETWEEN -180 AND 180)) "
            "OR (last_location_accuracy IS NOT NULL "
            "AND NOT (last_location_accuracy BETWEEN 0 AND 100000)) "
            "LIMIT 1"
        )
    ).first()
    if invalid is not None:
        raise RuntimeError(
            "0013 cannot add users location CHECK constraints while an invalid row exists: "
            f"{tuple(invalid)}"
        )


def _assert_binary_values_can_downgrade() -> None:
    bind = op.get_bind()
    identity_collision = bind.execute(
        sa.text(
            "SELECT 1 FROM user_identities "
            "GROUP BY provider, provider_subject COLLATE utf8mb4_0900_ai_ci "
            "HAVING COUNT(*) > 1 LIMIT 1"
        )
    ).first()
    nonce_collision = bind.execute(
        sa.text(
            "SELECT 1 FROM login_nonces "
            "GROUP BY nonce COLLATE utf8mb4_0900_ai_ci "
            "HAVING COUNT(*) > 1 LIMIT 1"
        )
    ).first()
    if identity_collision is not None or nonce_collision is not None:
        raise RuntimeError(
            "0013 downgrade would collapse case-distinct OIDC values under the legacy "
            "case-insensitive collation"
        )


def upgrade() -> None:
    # MySQL DDL auto-commits. Run every data-dependent guard before the first ALTER so a
    # rejected migration cannot leave a partially changed schema with revision 0012 recorded.
    _assert_site_settings_are_valid()
    _assert_user_locations_are_valid()

    # OIDC opaque values must retain byte/case distinctions in comparisons and unique indexes.
    op.alter_column(
        "user_identities",
        "provider_subject",
        existing_type=mysql.VARCHAR(length=255, collation=_LEGACY_COLLATION),
        type_=mysql.VARCHAR(length=255, collation=_BINARY_COLLATION),
        existing_nullable=False,
    )
    op.alter_column(
        "login_nonces",
        "nonce",
        existing_type=mysql.VARCHAR(length=64, collation=_LEGACY_COLLATION),
        type_=mysql.VARCHAR(length=64, collation=_BINARY_COLLATION),
        existing_nullable=False,
    )

    # Refuse to hide pre-existing corruption; operators must repair it intentionally first.
    # MySQL forbids CHECK constraints from referencing AUTO_INCREMENT columns. This singleton
    # is seeded explicitly as id=1 and should never allocate another identifier anyway.
    op.alter_column(
        "site_settings",
        "id",
        existing_type=mysql.INTEGER(),
        existing_nullable=False,
        autoincrement=False,
    )
    op.create_check_constraint(op.f("ck_site_settings_singleton_id"), "site_settings", "id = 1")
    op.create_check_constraint(
        op.f("ck_site_settings_signup_mode_valid"),
        "site_settings",
        "signup_mode IN ('open', 'approval', 'closed')",
    )
    op.create_check_constraint(
        op.f("ck_site_settings_provider_available"),
        "site_settings",
        "allow_google = 1 OR allow_microsoft = 1",
    )
    op.create_check_constraint(
        op.f("ck_site_settings_allow_google_boolean"),
        "site_settings",
        "allow_google IN (0, 1)",
    )
    op.create_check_constraint(
        op.f("ck_site_settings_allow_microsoft_boolean"),
        "site_settings",
        "allow_microsoft IN (0, 1)",
    )
    op.create_check_constraint(
        op.f("ck_site_settings_calm_mode_boolean"),
        "site_settings",
        "calm_mode IN (0, 1)",
    )

    op.create_check_constraint(
        op.f("ck_users_last_latitude_valid"),
        "users",
        "last_latitude IS NULL OR last_latitude BETWEEN -90 AND 90",
    )
    op.create_check_constraint(
        op.f("ck_users_last_longitude_valid"),
        "users",
        "last_longitude IS NULL OR last_longitude BETWEEN -180 AND 180",
    )
    op.create_check_constraint(
        op.f("ck_users_last_location_accuracy_valid"),
        "users",
        "last_location_accuracy IS NULL OR last_location_accuracy BETWEEN 0 AND 100000",
    )

    # Existing tokens used an eight-hour default but did not persist `exp`; preserve that
    # historical approximation, then make the lifecycle field mandatory for every new row.
    op.add_column(
        "user_sessions",
        sa.Column("expires_at", mysql.DATETIME(fsp=6), nullable=True),
    )
    op.execute(
        sa.text(
            "UPDATE user_sessions "
            "SET expires_at = DATE_ADD(created_at, INTERVAL 8 HOUR) "
            "WHERE expires_at IS NULL"
        )
    )
    op.alter_column(
        "user_sessions",
        "expires_at",
        existing_type=mysql.DATETIME(fsp=6),
        nullable=False,
    )
    op.create_index(
        "ix_user_sessions_user_revoked_created",
        "user_sessions",
        ["user_id", "revoked_at", "created_at"],
    )
    op.create_index("ix_user_sessions_expires_at", "user_sessions", ["expires_at"])
    # The composite index has the same leading FK column and supersedes this single-column key.
    op.drop_index("ix_user_sessions_user_id", table_name="user_sessions")

    op.alter_column(
        "request_logs",
        "id",
        existing_type=mysql.INTEGER(),
        type_=mysql.BIGINT(),
        existing_nullable=False,
        autoincrement=True,
    )
    op.create_index("ix_request_logs_user_id_id", "request_logs", ["user_id", "id"])
    op.create_index("ix_request_logs_method_id", "request_logs", ["method", "id"])
    op.create_index("ix_users_created_at_id", "users", ["created_at", "id"])


def downgrade() -> None:
    # All downgrade refusal conditions must run before MySQL's first auto-committing DDL.
    _assert_binary_values_can_downgrade()
    max_log_id = op.get_bind().execute(sa.text("SELECT MAX(id) FROM request_logs")).scalar()
    if max_log_id is not None and max_log_id > 2_147_483_647:
        raise RuntimeError("0013 downgrade cannot fit request_logs.id values into signed INT")

    op.drop_index("ix_users_created_at_id", table_name="users")
    op.drop_index("ix_request_logs_method_id", table_name="request_logs")
    op.drop_index("ix_request_logs_user_id_id", table_name="request_logs")

    op.alter_column(
        "request_logs",
        "id",
        existing_type=mysql.BIGINT(),
        type_=mysql.INTEGER(),
        existing_nullable=False,
        autoincrement=True,
    )

    # Keep an index available for the FK before removing the replacement composite index.
    op.create_index("ix_user_sessions_user_id", "user_sessions", ["user_id"])
    op.drop_index("ix_user_sessions_expires_at", table_name="user_sessions")
    op.drop_index("ix_user_sessions_user_revoked_created", table_name="user_sessions")
    op.drop_column("user_sessions", "expires_at")

    op.drop_constraint(
        op.f("ck_users_last_location_accuracy_valid"),
        "users",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_users_last_longitude_valid"),
        "users",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_users_last_latitude_valid"),
        "users",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_site_settings_calm_mode_boolean"),
        "site_settings",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_site_settings_allow_microsoft_boolean"),
        "site_settings",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_site_settings_allow_google_boolean"),
        "site_settings",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_site_settings_provider_available"),
        "site_settings",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_site_settings_signup_mode_valid"),
        "site_settings",
        type_="check",
    )
    op.drop_constraint(
        op.f("ck_site_settings_singleton_id"),
        "site_settings",
        type_="check",
    )
    op.alter_column(
        "site_settings",
        "id",
        existing_type=mysql.INTEGER(),
        existing_nullable=False,
        autoincrement=True,
    )

    op.alter_column(
        "login_nonces",
        "nonce",
        existing_type=mysql.VARCHAR(length=64, collation=_BINARY_COLLATION),
        type_=mysql.VARCHAR(length=64, collation=_LEGACY_COLLATION),
        existing_nullable=False,
    )
    op.alter_column(
        "user_identities",
        "provider_subject",
        existing_type=mysql.VARCHAR(length=255, collation=_BINARY_COLLATION),
        type_=mysql.VARCHAR(length=255, collation=_LEGACY_COLLATION),
        existing_nullable=False,
    )
