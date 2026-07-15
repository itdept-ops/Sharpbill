"""Test setup: run the whole suite against a dedicated `*_test` MySQL database.

The test DB URL is derived from DATABASE_URL (or TEST_DATABASE_URL) and installed into the
environment BEFORE any app module is imported, so app.config, app.db, and Alembic all target
the test database. Schema is created by running the real migrations (never create_all).
"""

import os

from sqlalchemy import create_engine, select, text
from sqlalchemy.engine import make_url

# --- Redirect to the test database before importing app code ---------------------------
_main_url = make_url(os.environ["DATABASE_URL"])
_test_url_str = os.environ.get("TEST_DATABASE_URL")
_test_url = (
    make_url(_test_url_str)
    if _test_url_str
    else _main_url.set(database=(_main_url.database or "appdb") + "_test")
)

os.environ["DATABASE_URL"] = _test_url.render_as_string(hide_password=False)
os.environ.setdefault("APP_ENV", "local")
os.environ["DEV_AUTH_ENABLED"] = "true"  # exercise the HTTP stack via /api/auth/dev
os.environ.setdefault("SESSION_JWT_SECRET", "test-secret-0123456789abcdef0123456789abcdef")

# Create the test database and grant the app user access. Connect to an already-existing
# database (the main one) as root, since the app user usually cannot CREATE DATABASE.
# (URL.set(database=None) does NOT clear the name, so we start from the main URL instead.)
_db_name = _test_url.database
_root_pw = os.environ.get("MYSQL_ROOT_PASSWORD")
if _root_pw:
    _admin_url = _main_url.set(username="root", password=_root_pw)
else:
    _admin_url = _main_url  # fall back to the app user on the main DB (must have rights)

_admin_engine = create_engine(_admin_url)
with _admin_engine.connect() as conn:
    conn.execute(
        text(
            f"CREATE DATABASE IF NOT EXISTS `{_db_name}` "
            "CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"
        )
    )
    if _root_pw and _test_url.username and _test_url.username != "root":
        conn.execute(text(f"GRANT ALL PRIVILEGES ON `{_db_name}`.* TO '{_test_url.username}'@'%'"))
        conn.execute(text("FLUSH PRIVILEGES"))
    conn.commit()
_admin_engine.dispose()

import pytest  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402

from alembic import command  # noqa: E402
from alembic.config import Config  # noqa: E402
from app.db import SessionLocal, engine  # noqa: E402
from app.main import app  # noqa: E402
from app.models import Role, User, UserIdentity  # noqa: E402
from app.ratelimit import reset as reset_ratelimit  # noqa: E402


@pytest.fixture(scope="session", autouse=True)
def _migrate() -> None:
    command.upgrade(Config("alembic.ini"), "head")


@pytest.fixture(autouse=True)
def _clean_tables():
    # Reset between tests: wipe users + any custom roles/permissions, then restore the
    # canonical system role<->permission seed so RBAC starts identical for every test.
    reset_ratelimit()  # each test starts with a fresh per-IP rate-limit window
    with engine.begin() as conn:
        # Reset the site-settings singleton first (its FK to roles would otherwise block the
        # custom-role cleanup below).
        conn.execute(
            text(
                "UPDATE site_settings SET signup_mode='open', allow_google=1, allow_microsoft=1, "
                "default_role_id=(SELECT id FROM roles WHERE name='user') WHERE id=1"
            )
        )
        conn.execute(text("DELETE FROM request_logs"))
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
    )
    db.add(user)
    db.flush()
    db.add(
        UserIdentity(
            user=user,
            provider=provider,
            provider_subject=email.lower(),
            provider_email=email.lower(),
        )
    )
    db.commit()
    db.refresh(user)
    return user
