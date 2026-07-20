from datetime import UTC, datetime, timedelta

import pytest
from sqlalchemy import BigInteger, inspect, select, text
from sqlalchemy.exc import DBAPIError

from alembic import command
from alembic.config import Config
from app.config import Settings, settings
from app.db import (
    engine,
    mysql_connect_args,
    mysql_migration_connect_args,
    runtime_engine_options,
)
from app.models import (
    LoginNonce,
    Permission,
    Role,
    SiteSettings,
    User,
    UserIdentity,
    UserSession,
)

_GOOD_SECRET = "test-secret-0123456789abcdef0123456789abcdef"


def _alembic_config() -> Config:
    return Config("alembic.ini")


def test_runtime_database_policy_has_utc_timeouts_pool_and_verified_tls():
    local_args = mysql_connect_args(settings)
    assert local_args["init_command"] == "SET time_zone = '+00:00'"
    assert local_args["connect_timeout"] == settings.db_connect_timeout_seconds
    assert local_args["read_timeout"] == settings.db_read_timeout_seconds
    assert local_args["write_timeout"] == settings.db_write_timeout_seconds

    pool_options = runtime_engine_options(settings)
    assert pool_options["pool_size"] == settings.db_pool_size
    assert pool_options["max_overflow"] == settings.db_max_overflow
    assert pool_options["pool_timeout"] == settings.db_pool_timeout_seconds

    migration_args = mysql_migration_connect_args(settings)
    assert migration_args["connect_timeout"] == local_args["connect_timeout"]
    assert migration_args["init_command"] == local_args["init_command"]
    assert migration_args.get("ssl") == local_args.get("ssl")
    assert "read_timeout" not in migration_args
    assert "write_timeout" not in migration_args

    tls_settings = Settings(
        database_url="mysql+pymysql://appuser:apppass@db.example/appdb",
        session_jwt_secret=_GOOD_SECRET,
        app_env="production",
        cookie_secure=True,
        db_require_tls=True,
        db_tls_ca_path="/trusted/company-db-ca.pem",
        public_origin="https://crm.example.com",
        allow_public_signup=False,
        google_client_id="123456-testclient.apps.googleusercontent.com",
        allowed_email_domains="example.com",
        azure_client_id="",
        admin_emails="",
        azure_admin_tenant_id="",
        azure_admin_object_ids="",
    )
    tls_args = mysql_connect_args(tls_settings)
    assert tls_args["ssl"] == {
        "ca": "/trusted/company-db-ca.pem",
        "check_hostname": True,
    }


def test_real_mysql_schema_contracts_are_materialized():
    inspector = inspect(engine)
    identity_columns = {
        column["name"]: column for column in inspector.get_columns("user_identities")
    }
    nonce_columns = {column["name"]: column for column in inspector.get_columns("login_nonces")}
    session_columns = {column["name"]: column for column in inspector.get_columns("user_sessions")}
    log_columns = {column["name"]: column for column in inspector.get_columns("request_logs")}
    settings_columns = {column["name"]: column for column in inspector.get_columns("site_settings")}
    role_columns = {column["name"]: column for column in inspector.get_columns("roles")}
    user_columns = {column["name"]: column for column in inspector.get_columns("users")}

    assert identity_columns["provider_subject"]["type"].collation == "utf8mb4_0900_bin"
    assert identity_columns["provider_tenant_id"]["nullable"] is True
    assert identity_columns["provider_hosted_domain"]["nullable"] is True
    assert nonce_columns["nonce"]["type"].collation == "utf8mb4_0900_bin"
    assert session_columns["expires_at"]["nullable"] is False
    assert isinstance(log_columns["id"]["type"], BigInteger)
    assert settings_columns["id"]["autoincrement"] is False
    assert role_columns["version"]["nullable"] is False
    assert user_columns["access_version"]["nullable"] is False

    check_names = {
        constraint["name"] for constraint in inspector.get_check_constraints("site_settings")
    }
    assert {
        "ck_site_settings_singleton_id",
        "ck_site_settings_signup_mode_valid",
        "ck_site_settings_provider_available",
        "ck_site_settings_allow_google_boolean",
        "ck_site_settings_allow_microsoft_boolean",
        "ck_site_settings_calm_mode_boolean",
    } <= check_names
    user_check_names = {
        constraint["name"] for constraint in inspector.get_check_constraints("users")
    }
    assert {
        "ck_users_last_latitude_valid",
        "ck_users_last_longitude_valid",
        "ck_users_last_location_accuracy_valid",
        "ck_users_is_active_boolean",
        "ck_users_is_approved_boolean",
    } <= user_check_names
    role_check_names = {
        constraint["name"] for constraint in inspector.get_check_constraints("roles")
    }
    permission_check_names = {
        constraint["name"] for constraint in inspector.get_check_constraints("permissions")
    }
    assert "ck_roles_is_system_boolean" in role_check_names
    assert "ck_permissions_is_system_boolean" in permission_check_names

    index_names = {
        table: {index["name"] for index in inspector.get_indexes(table)}
        for table in ("users", "user_sessions", "request_logs")
    }
    assert "ix_users_created_at_id" in index_names["users"]
    assert "ix_user_sessions_user_revoked_created" in index_names["user_sessions"]
    assert "ix_user_sessions_expires_at" in index_names["user_sessions"]
    assert "ix_user_sessions_revoked_at" in index_names["user_sessions"]
    assert "ix_request_logs_user_id_id" in index_names["request_logs"]
    assert "ix_request_logs_method_id" in index_names["request_logs"]
    assert "ix_request_logs_user_id" not in index_names["request_logs"]

    with engine.connect() as connection:
        assert connection.scalar(text("SELECT @@SESSION.time_zone")) == "+00:00"


@pytest.mark.parametrize("model", [Permission, Role, SiteSettings, User, UserIdentity])
def test_server_generated_update_timestamps_are_mapped_for_refresh(model):
    updated_at = model.__table__.c.updated_at
    assert updated_at.type.timezone is False
    assert updated_at.server_default is not None
    assert updated_at.server_onupdate is not None


def test_oidc_subjects_and_nonces_are_case_sensitive_on_real_mysql(db):
    role = db.scalar(select(Role).where(Role.name == "user"))
    assert role is not None

    first = User(email="first@example.com", display_name="First", role=role)
    second = User(email="second@example.com", display_name="Second", role=role)
    db.add_all([first, second])
    db.flush()
    db.add_all(
        [
            UserIdentity(
                user=first,
                provider="google",
                provider_subject="OpaqueSubjectABC",
                provider_email=first.email,
            ),
            UserIdentity(
                user=second,
                provider="google",
                provider_subject="opaquesubjectabc",
                provider_email=second.email,
            ),
            LoginNonce(
                nonce="OpaqueNonceABC",
                expires_at=datetime.now(UTC).replace(tzinfo=None) + timedelta(minutes=5),
            ),
            LoginNonce(
                nonce="opaquenonceabc",
                expires_at=datetime.now(UTC).replace(tzinfo=None) + timedelta(minutes=5),
            ),
        ]
    )
    db.commit()

    subject_count = db.scalar(
        text(
            "SELECT COUNT(*) FROM user_identities "
            "WHERE provider = 'google' AND provider_subject = 'OpaqueSubjectABC'"
        )
    )
    nonce_count = db.scalar(
        text("SELECT COUNT(*) FROM login_nonces WHERE nonce = 'OpaqueNonceABC'")
    )
    assert subject_count == 1
    assert nonce_count == 1


@pytest.mark.parametrize(
    "statement",
    [
        "UPDATE site_settings SET allow_google=0, allow_microsoft=0 WHERE id=1",
        "UPDATE site_settings SET signup_mode='invalid' WHERE id=1",
        "UPDATE site_settings SET allow_google=2 WHERE id=1",
        "UPDATE site_settings SET allow_microsoft=2 WHERE id=1",
        "UPDATE site_settings SET calm_mode=2 WHERE id=1",
        (
            "INSERT INTO site_settings "
            "(id, signup_mode, allow_google, allow_microsoft, default_role_id, calm_mode) "
            "SELECT 2, 'open', 1, 1, id, 0 FROM roles WHERE name='user'"
        ),
    ],
)
def test_site_settings_checks_reject_invalid_direct_sql(statement):
    with pytest.raises(DBAPIError):
        with engine.begin() as connection:
            connection.execute(text(statement))


@pytest.mark.parametrize(
    ("column", "value"),
    [
        ("last_latitude", -91),
        ("last_latitude", 91),
        ("last_longitude", -181),
        ("last_longitude", 181),
        ("last_location_accuracy", -1),
        ("last_location_accuracy", 100_001),
    ],
)
def test_user_location_checks_reject_invalid_direct_sql(column, value):
    statement = text(
        "INSERT INTO users (email, display_name, role_id, "
        f"{column}) SELECT :email, 'Invalid location', id, :value "
        "FROM roles WHERE name='user'"
    )
    with pytest.raises(DBAPIError):
        with engine.begin() as connection:
            connection.execute(
                statement,
                {"email": f"invalid-{column}-{value}@example.com", "value": value},
            )


@pytest.mark.parametrize(
    "statement",
    [
        "UPDATE users SET is_active=2 LIMIT 1",
        "UPDATE users SET is_approved=2 LIMIT 1",
        "UPDATE roles SET is_system=2 LIMIT 1",
        "UPDATE permissions SET is_system=2 LIMIT 1",
    ],
)
def test_lifecycle_boolean_checks_reject_invalid_direct_sql(db, statement):
    role = db.scalar(select(Role).where(Role.name == "user"))
    assert role is not None
    db.add(User(email="boolean-check@example.com", display_name="Boolean", role=role))
    db.commit()
    with pytest.raises(DBAPIError):
        with engine.begin() as connection:
            connection.execute(text(statement))


def test_permission_descriptions_match_the_canonical_catalog(db):
    descriptions = dict(db.execute(select(Permission.key, Permission.description)).all())
    assert descriptions["users.manage"] == "Manage user profiles, activation, and approval"
    assert descriptions["settings.manage"] == "Manage site-wide configuration"
    assert descriptions["users.export"] == "Export the user directory as CSV"
    assert descriptions["security_events.view"] == "View and export durable security events"


def test_new_session_gets_configured_utc_naive_expiry(db):
    role = db.scalar(select(Role).where(Role.name == "user"))
    assert role is not None
    user = User(email="expiry@example.com", display_name="Expiry", role=role)
    db.add(user)
    db.flush()

    before = datetime.now(UTC).replace(tzinfo=None)
    session = UserSession(user_id=user.id, jti="expiry-contract-jti")
    db.add(session)
    db.flush()
    after = datetime.now(UTC).replace(tzinfo=None)

    assert session.expires_at.tzinfo is None
    expected_delta = timedelta(seconds=settings.session_ttl_seconds)
    assert before + expected_delta <= session.expires_at <= after + expected_delta


def test_0013_backfills_legacy_session_expiry_and_round_trips():
    engine.dispose()
    try:
        command.downgrade(_alembic_config(), "0012")
        legacy_created = datetime(2026, 7, 20, 12, 0, 0)
        with engine.begin() as connection:
            role_id = connection.scalar(text("SELECT id FROM roles WHERE name='user'"))
            result = connection.execute(
                text(
                    "INSERT INTO users (email, display_name, role_id) "
                    "VALUES ('legacy-session@example.com', 'Legacy session', :role_id)"
                ),
                {"role_id": role_id},
            )
            user_id = result.lastrowid
            connection.execute(
                text(
                    "INSERT INTO user_sessions (user_id, jti, created_at) "
                    "VALUES (:user_id, 'legacy-session-jti', :created_at)"
                ),
                {"user_id": user_id, "created_at": legacy_created},
            )

        command.upgrade(_alembic_config(), "head")
        with engine.connect() as connection:
            expires_at = connection.scalar(
                text("SELECT expires_at FROM user_sessions WHERE jti='legacy-session-jti'")
            )
        assert expires_at == legacy_created + timedelta(hours=8)
    finally:
        command.upgrade(_alembic_config(), "head")
        engine.dispose()


def test_0015_refuses_legacy_admin_signup_default_before_any_ddl():
    engine.dispose()
    try:
        command.downgrade(_alembic_config(), "0014")
        with engine.connect() as connection:
            legacy_description = connection.scalar(
                text("SELECT description FROM permissions WHERE `key`='users.manage'")
            )
        assert legacy_description == "Change user roles and activation status"
        with engine.begin() as connection:
            connection.execute(
                text(
                    "UPDATE site_settings SET default_role_id="
                    "(SELECT id FROM roles WHERE name='admin') WHERE id=1"
                )
            )

        with pytest.raises(RuntimeError, match="protected admin role"):
            command.upgrade(_alembic_config(), "head")

        with engine.connect() as connection:
            assert connection.scalar(text("SELECT version_num FROM alembic_version")) == "0014"
            indexes = {
                row[0]
                for row in connection.execute(
                    text(
                        "SHOW INDEX FROM user_sessions WHERE Key_name='ix_user_sessions_revoked_at'"
                    )
                )
            }
            assert not indexes
        with engine.begin() as connection:
            connection.execute(
                text(
                    "UPDATE site_settings SET default_role_id="
                    "(SELECT id FROM roles WHERE name='user') WHERE id=1"
                )
            )
        command.upgrade(_alembic_config(), "head")
        with engine.connect() as connection:
            current_description = connection.scalar(
                text("SELECT description FROM permissions WHERE `key`='users.manage'")
            )
        assert current_description == "Manage user profiles, activation, and approval"
    finally:
        command.upgrade(_alembic_config(), "head")
        engine.dispose()


def test_0016_least_privilege_permissions_round_trip_and_protect_retained_grants():
    engine.dispose()
    try:
        with engine.begin() as connection:
            connection.execute(
                text(
                    "INSERT INTO roles (name, description, is_system) "
                    "VALUES ('EvidenceReader', 'migration downgrade guard', 0)"
                )
            )
            connection.execute(
                text(
                    "INSERT INTO role_permissions (role_id, permission_id) "
                    "SELECT r.id, p.id FROM roles r JOIN permissions p "
                    "WHERE r.name='EvidenceReader' AND p.`key`='security_events.view'"
                )
            )

        with pytest.raises(RuntimeError, match="retained grants"):
            command.downgrade(_alembic_config(), "0015")
        with engine.connect() as connection:
            assert connection.scalar(text("SELECT version_num FROM alembic_version")) == "0016"

        with engine.begin() as connection:
            connection.execute(
                text(
                    "DELETE rp FROM role_permissions rp JOIN roles r ON r.id=rp.role_id "
                    "WHERE r.name='EvidenceReader'"
                )
            )
            connection.execute(text("DELETE FROM roles WHERE name='EvidenceReader'"))

        command.downgrade(_alembic_config(), "0015")
        with engine.connect() as connection:
            assert connection.scalar(text("SELECT version_num FROM alembic_version")) == "0015"
            assert (
                connection.scalar(
                    text(
                        "SELECT COUNT(*) FROM permissions "
                        "WHERE `key` IN ('users.export', 'security_events.view')"
                    )
                )
                == 0
            )

        command.upgrade(_alembic_config(), "head")
        with engine.connect() as connection:
            seeded = connection.execute(
                text(
                    "SELECT p.`key` FROM role_permissions rp "
                    "JOIN roles r ON r.id=rp.role_id "
                    "JOIN permissions p ON p.id=rp.permission_id "
                    "WHERE r.name='admin' "
                    "AND p.`key` IN ('users.export', 'security_events.view')"
                )
            ).scalars()
            assert set(seeded) == {"users.export", "security_events.view"}
    finally:
        command.upgrade(_alembic_config(), "head")
        engine.dispose()


def test_0017_refuses_to_discard_persisted_identity_admission_authority():
    engine.dispose()
    try:
        with engine.begin() as connection:
            role_id = connection.scalar(text("SELECT id FROM roles WHERE name='admin'"))
            result = connection.execute(
                text(
                    "INSERT INTO users (email, display_name, role_id) "
                    "VALUES ('migration-authority@example.com', 'Migration authority', :role_id)"
                ),
                {"role_id": role_id},
            )
            connection.execute(
                text(
                    "INSERT INTO user_identities "
                    "(user_id, provider, provider_subject, provider_email, "
                    "provider_hosted_domain) VALUES "
                    "(:user_id, 'google', 'migration-authority-subject', "
                    "'migration-authority@example.com', 'example.com')"
                ),
                {"user_id": result.lastrowid},
            )

        with pytest.raises(RuntimeError, match="admission authority would be lost"):
            command.downgrade(_alembic_config(), "0016")

        with engine.connect() as connection:
            assert connection.scalar(text("SELECT version_num FROM alembic_version")) == "0017"
            assert (
                connection.scalar(
                    text(
                        "SELECT provider_hosted_domain FROM user_identities "
                        "WHERE provider_subject='migration-authority-subject'"
                    )
                )
                == "example.com"
            )

        with engine.begin() as connection:
            connection.execute(
                text(
                    "UPDATE user_identities SET provider_tenant_id=NULL, "
                    "provider_hosted_domain=NULL "
                    "WHERE provider_subject='migration-authority-subject'"
                )
            )
        command.downgrade(_alembic_config(), "0016")
        with engine.connect() as connection:
            identity_columns = {
                column["name"] for column in inspect(connection).get_columns("user_identities")
            }
            assert "provider_tenant_id" not in identity_columns
            assert "provider_hosted_domain" not in identity_columns

        command.upgrade(_alembic_config(), "head")
    finally:
        command.upgrade(_alembic_config(), "head")
        engine.dispose()


def test_alembic_reports_no_model_drift_on_real_mysql():
    command.check(_alembic_config())
