"""Test setup: run the whole suite against a dedicated `*_test` MySQL database.

The test DB URL is derived from DATABASE_URL (or TEST_DATABASE_URL) and installed into the
environment BEFORE any app module is imported, so app.config, app.db, and Alembic all target
the test database. Schema is created by running the real migrations (never create_all).
"""

import os
from datetime import UTC, datetime

from sqlalchemy import create_engine, select, text
from sqlalchemy.engine import URL, make_url

from tests.db_safety import randomized_test_database_url, validate_test_database_base

# --- Redirect to the test database before importing app code ---------------------------
_configured_database_url = os.environ.get("DATABASE_URL")
if _configured_database_url:
    _main_url = make_url(_configured_database_url)
else:
    # Compose deliberately supplies separate fields so URL-reserved credential characters are
    # encoded safely and the API receives no root secret. Mirror app.config before importing it.
    _main_url = URL.create(
        "mysql+pymysql",
        username=os.environ["DB_USER"],
        password=os.environ["DB_PASSWORD"],
        host=os.environ["DB_HOST"],
        port=int(os.environ.get("DB_PORT", "3306")),
        database=os.environ["DB_NAME"],
        query={"charset": "utf8mb4"},
    )
_test_url_str = os.environ.get("TEST_DATABASE_URL")
_candidate_url = (
    make_url(_test_url_str)
    if _test_url_str
    else _main_url.set(database=(_main_url.database or "appdb") + "_test")
)
_test_base_url = validate_test_database_base(
    _main_url,
    _candidate_url,
    destructive_ack=os.environ.get("TEST_DATABASE_DESTRUCTIVE_ACK"),
)
_test_url = randomized_test_database_url(_test_base_url)
_db_name = _test_url.database
assert _db_name is not None  # guaranteed by validate_test_database_base

os.environ["DATABASE_URL"] = _test_url.render_as_string(hide_password=False)
os.environ.setdefault("APP_ENV", "local")
os.environ["DEV_AUTH_ENABLED"] = "true"  # exercise the HTTP stack via /api/auth/dev
os.environ["DEV_AUTH_SECRET"] = "test-dev-auth-secret-0123456789abcdef-EXPLICIT"
os.environ["GOOGLE_CLIENT_ID"] = "test-google-client-id"
os.environ["AZURE_CLIENT_ID"] = "22222222-3333-4444-8555-666666666666"
os.environ["TRUSTED_PROXY_IPS"] = "127.0.0.1"
os.environ.setdefault("SESSION_JWT_SECRET", "test-secret-0123456789abcdef0123456789abcdef")

# Create the test database and grant the app user access. Connect to an already-existing
# database (the main one) as root, since the app user usually cannot CREATE DATABASE.
# (URL.set(database=None) does NOT clear the name, so we start from the main URL instead.)
_root_pw = os.environ.get("MYSQL_ROOT_PASSWORD")
if _root_pw:
    _admin_url = _main_url.set(username="root", password=_root_pw)
else:
    _admin_url = _main_url  # fall back to the app user on the main DB (must have rights)


def _create_test_database() -> None:
    admin_engine = create_engine(_admin_url)
    try:
        with admin_engine.begin() as conn:
            conn.execute(
                text(
                    f"CREATE DATABASE `{_db_name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"
                )
            )
            if _root_pw and _test_url.username:
                conn.execute(
                    text(f"GRANT ALL PRIVILEGES ON `{_db_name}`.* TO '{_test_url.username}'@'%'")
                )
                conn.execute(text("FLUSH PRIVILEGES"))
    finally:
        admin_engine.dispose()


def _drop_test_database() -> None:
    cleanup_engine = create_engine(_admin_url)
    try:
        with cleanup_engine.begin() as conn:
            conn.execute(text(f"DROP DATABASE IF EXISTS `{_db_name}`"))
    finally:
        cleanup_engine.dispose()


import pytest  # noqa: E402

from alembic import command  # noqa: E402
from alembic.config import Config  # noqa: E402
from alembic.script import ScriptDirectory  # noqa: E402
from app.db import SessionLocal, engine  # noqa: E402
from app.main import app  # noqa: E402
from app.models import Role, User, UserIdentity  # noqa: E402
from app.ratelimit import reset as reset_ratelimit  # noqa: E402
from app.request_logging import flush_request_logging  # noqa: E402
from app.routers.dashboard import _reset_analytics_cache  # noqa: E402
from app.routers.health import _reset_readiness_cache  # noqa: E402
from tests.client import TestClient  # noqa: E402


@pytest.fixture(scope="session", autouse=True)
def _migrate():
    created = False
    try:
        _create_test_database()
        created = True
        alembic_config = Config("alembic.ini")
        command.upgrade(alembic_config, "head")
        yield
        expected_heads = frozenset(ScriptDirectory.from_config(alembic_config).get_heads())
        with engine.connect() as connection:
            actual_heads = frozenset(
                connection.execute(text("SELECT version_num FROM alembic_version")).scalars()
            )
        assert actual_heads == expected_heads, "test suite leaked the database away from head"
        command.check(alembic_config)
    finally:
        flush_request_logging(timeout=5.0)
        engine.dispose()
        if created:
            _drop_test_database()


@pytest.fixture(autouse=True)
def _clean_tables():
    # Reset between tests: wipe users + any custom roles/permissions, then restore the
    # canonical system role<->permission seed so RBAC starts identical for every test.
    reset_ratelimit()  # each test starts with a fresh per-IP rate-limit window
    _reset_analytics_cache()  # and a cold analytics cache (so counts reflect this test's DB)
    _reset_readiness_cache()  # readiness snapshots must not cross isolated database fixtures
    # Fence the asynchronous writer so a late prior-test commit cannot cross this cleanup.
    flush_request_logging(timeout=5.0)
    with engine.begin() as conn:
        # Reset the site-settings singleton first (its FK to roles would otherwise block the
        # custom-role cleanup below).
        conn.execute(
            text(
                "UPDATE site_settings SET signup_mode='open', allow_google=1, allow_microsoft=1, "
                "calm_mode=0, retention_hold=0, retention_hold_reference=NULL, "
                "default_role_id=(SELECT id FROM roles WHERE name='user') WHERE id=1"
            )
        )
        conn.execute(text("DELETE FROM login_nonces"))
        conn.execute(text("DELETE FROM security_event_deliveries"))
        conn.execute(text("DELETE FROM security_events"))
        conn.execute(text("DELETE FROM request_logs"))
        conn.execute(text("DELETE FROM user_sessions"))
        conn.execute(text("DELETE FROM legal_acceptances"))
        conn.execute(text("DELETE FROM user_permissions"))
        conn.execute(text("DELETE FROM user_identities"))
        conn.execute(text("DELETE FROM users"))
        conn.execute(text("DELETE FROM roles WHERE is_system = 0"))
        conn.execute(text("DELETE FROM permissions WHERE is_system = 0"))
        conn.execute(text("DELETE FROM role_permissions"))
        conn.execute(
            text(
                "INSERT INTO role_permissions (role_id, permission_id) "
                "SELECT r.id, p.id FROM roles r JOIN permissions p "
                "WHERE r.name = 'admin' AND p.is_system = 1"
            )
        )
        conn.execute(
            text(
                "INSERT INTO role_permissions (role_id, permission_id) "
                "SELECT r.id, p.id FROM roles r JOIN permissions p "
                "WHERE r.name = 'user' AND p.key IN ('presence.view')"
            )
        )
    yield


@pytest.fixture
def db():
    session = SessionLocal()
    try:
        yield session
    finally:
        session.close()


@pytest.fixture
def client():
    return TestClient(app)


def make_user(db, *, email="user@example.com", role="user", is_active=True, provider="dev") -> User:
    role_obj = db.scalar(select(Role).where(Role.name == role))
    user = User(
        email=email.lower(),
        display_name=email.split("@")[0],
        role=role_obj,
        is_active=is_active,
        deactivated_at=None if is_active else datetime.now(UTC).replace(tzinfo=None),
    )
    db.add(user)
    db.flush()
    db.add(
        UserIdentity(
            user=user,
            provider=provider,
            provider_subject=email.lower(),
        )
    )
    db.commit()
    db.refresh(user)
    return user
