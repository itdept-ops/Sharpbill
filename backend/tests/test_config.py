"""Unit tests for the security-critical config guards (FND-026): secret strength, the prod
secure-cookie invariant, and the local-only dev-auth gate."""

import pytest
from pydantic import ValidationError

from app.config import Settings


def _base(**over):
    env = {
        "database_url": "mysql+pymysql://u:p@h:3306/db",
        "session_jwt_secret": "a" * 40,
        "app_env": "local",
        "cookie_secure": True,
    }
    env.update(over)
    return env


def test_secret_too_short_is_rejected():
    with pytest.raises(ValidationError):
        Settings(**_base(session_jwt_secret="short"))


def test_placeholder_secret_is_rejected():
    with pytest.raises(ValidationError):
        Settings(**_base(session_jwt_secret="replace-me-" + "x" * 30))


def test_strong_secret_is_accepted():
    assert Settings(**_base()).session_jwt_secret == "a" * 40


def test_production_requires_secure_cookie():
    with pytest.raises(ValidationError):
        Settings(**_base(app_env="production", cookie_secure=False))


def test_dev_auth_never_enabled_outside_local():
    prod = Settings(**_base(app_env="production", cookie_secure=True, dev_auth_enabled=True))
    assert prod.is_dev_auth_enabled is False


def test_dev_auth_enabled_in_local_with_flag():
    local = Settings(**_base(app_env="local", dev_auth_enabled=True))
    assert local.is_dev_auth_enabled is True
