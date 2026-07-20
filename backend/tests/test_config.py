"""Unit tests for the security-critical config guards (FND-026): secret strength, the prod
secure-cookie invariant, and the local-only dev-auth gate."""

from pathlib import Path

import pytest
from pydantic import ValidationError
from sqlalchemy.engine import make_url

from app.config import Settings

_GOOD_SECRET = "test-secret-0123456789abcdef0123456789abcdef"
_TENANT_ID = "11111111-2222-4333-8444-555555555555"
_OBJECT_ID = "99999999-8888-4777-8666-555555555555"
_AZURE_CLIENT_ID = "22222222-3333-4444-8555-666666666666"
_GOOGLE_CLIENT_ID = "123456789012-abcdefghijklmnopqrstuvwxyz012345.apps.googleusercontent.com"


def _base(**over):
    env = {
        "database_url": "mysql+pymysql://u:p@h:3306/db",
        "session_jwt_secret": _GOOD_SECRET,
        "app_env": "local",
        "cookie_secure": True,
        # Isolate constructor tests from the integration-suite environment installed by conftest.
        "google_client_id": "",
        "azure_client_id": "",
        "admin_emails": "",
        "azure_admin_tenant_id": "",
        "azure_admin_object_ids": "",
        "public_origin": "https://crm.example.com",
        "trusted_proxy_ips": "",
    }
    env.update(over)
    return env


def test_secret_too_short_is_rejected():
    with pytest.raises(ValidationError):
        Settings(**_base(session_jwt_secret="short"))


def test_placeholder_secret_is_rejected():
    with pytest.raises(ValidationError):
        Settings(**_base(session_jwt_secret="replace-me-" + "x" * 30))


def test_low_entropy_secret_is_rejected():
    with pytest.raises(ValidationError):
        Settings(**_base(session_jwt_secret="a" * 40))  # long enough, but only one distinct char


def test_strong_secret_is_accepted():
    assert Settings(**_base()).session_jwt_secret == _GOOD_SECRET


def test_identity_provider_cache_and_backoff_windows_are_ordered():
    with pytest.raises(ValidationError, match="IDP_KEY_CACHE_STALE_SECONDS"):
        Settings(**_base(idp_key_cache_ttl_seconds=600, idp_key_cache_stale_seconds=300))
    with pytest.raises(ValidationError, match="IDP_OUTAGE_BACKOFF_MAX_SECONDS"):
        Settings(
            **_base(
                idp_outage_backoff_initial_seconds=10,
                idp_outage_backoff_max_seconds=5,
            )
        )


def test_production_requires_secure_cookie():
    with pytest.raises(ValidationError):
        Settings(**_base(app_env="production", cookie_secure=False, db_require_tls=True))


def test_production_requires_database_tls():
    with pytest.raises(ValidationError, match="DB_REQUIRE_TLS"):
        Settings(
            **_base(
                app_env="production",
                cookie_secure=True,
                db_require_tls=False,
                google_client_id=_GOOGLE_CLIENT_ID,
            )
        )


def test_production_requires_at_least_one_identity_provider():
    with pytest.raises(ValidationError, match="At least one identity provider"):
        Settings(**_base(app_env="production", cookie_secure=True, db_require_tls=True))


def test_production_requires_canonical_https_public_origin():
    with pytest.raises(ValidationError, match="PUBLIC_ORIGIN"):
        Settings(
            **_base(
                app_env="production",
                cookie_secure=True,
                db_require_tls=True,
                public_origin="http://crm.example.com/path",
                google_client_id=_GOOGLE_CLIENT_ID,
            )
        )

    for malformed in (
        "https://crm.example.com:notaport",
        "https://crm.example.com:70000",
        "https://[invalid",
    ):
        with pytest.raises(ValidationError, match="PUBLIC_ORIGIN"):
            Settings(
                **_base(
                    app_env="production",
                    cookie_secure=True,
                    db_require_tls=True,
                    public_origin=malformed,
                    google_client_id=_GOOGLE_CLIENT_ID,
                )
            )

    normalized = Settings(
        **_base(
            app_env="production",
            cookie_secure=True,
            db_require_tls=True,
            public_origin="https://CRM.EXAMPLE.COM:443/",
            google_client_id=_GOOGLE_CLIENT_ID,
        )
    )
    assert normalized.public_origin == "https://crm.example.com"


def test_validation_errors_never_echo_secret_inputs():
    jwt_sentinel = "jwt-SENTINEL-0123456789abcdef0123456789abcdef"
    dev_sentinel = "dev-SENTINEL-0123456789abcdef0123456789abcdef"
    password_sentinel = "db-password-SENTINEL"
    with pytest.raises(ValidationError) as caught:
        Settings(
            **_base(
                app_env="production",
                cookie_secure=False,
                db_require_tls=True,
                database_url=(f"mysql+pymysql://app:{password_sentinel}@database:3306/appdb"),
                session_jwt_secret=jwt_sentinel,
                dev_auth_secret=dev_sentinel,
                google_client_id=_GOOGLE_CLIENT_ID,
            )
        )
    rendered = str(caught.value)
    assert jwt_sentinel not in rendered
    assert dev_sentinel not in rendered
    assert password_sentinel not in rendered


def test_production_accepts_setting_driven_providers_without_directory_allowlists():
    configured = Settings(
        **_base(
            app_env="production",
            cookie_secure=True,
            db_require_tls=True,
            google_client_id=_GOOGLE_CLIENT_ID,
            azure_client_id=_AZURE_CLIENT_ID,
        )
    )
    assert configured.google_provider_configured is True
    assert configured.microsoft_provider_configured is True
    assert configured.session_cookie_name == "__Host-session"


def test_privacy_retention_defaults_are_conservative_and_bounded():
    configured = Settings(**_base())
    assert configured.precise_location_retention_hours == 24
    assert configured.pending_account_retention_days == 30
    assert configured.session_retention_days == 30
    assert configured.request_log_retention_days == 90
    assert configured.account_erasure_grace_days == 30
    assert configured.disabled_account_retention_days == 365
    assert configured.security_event_retention_days == 400
    assert configured.legal_acceptance_retention_days == 2555

    with pytest.raises(ValidationError):
        Settings(**_base(precise_location_retention_hours=0))
    with pytest.raises(ValidationError):
        Settings(**_base(account_erasure_grace_days=91))
    with pytest.raises(ValidationError):
        Settings(**_base(account_retention_prune_batch_size=1))
    with pytest.raises(ValidationError):
        Settings(**_base(legal_acceptance_retention_days=0))


def test_legal_acceptance_retention_environment_surfaces_match_settings():
    configured = Settings(
        **_base(
            legal_acceptance_retention_days=1825,
            legal_acceptance_prune_batch_size=750,
        )
    )
    assert configured.legal_acceptance_retention_days == 1825
    assert configured.legal_acceptance_prune_batch_size == 750

    repository = Path(__file__).resolve().parents[2]
    compose_path = repository / "docker-compose.yml"
    env_path = repository / ".env.example"
    # The normal CI checkout exposes the repository root. The local dev API container mounts only
    # ``backend`` at /app; its focused test runs still exercise the Settings fields above.
    if not compose_path.is_file() or not env_path.is_file():
        return
    compose = compose_path.read_text(encoding="utf-8")
    env_example = env_path.read_text(encoding="utf-8")
    for variable in (
        "LEGAL_ACCEPTANCE_RETENTION_DAYS",
        "LEGAL_ACCEPTANCE_PRUNE_BATCH_SIZE",
    ):
        assert f"{variable}: ${{{variable}:-" in compose
        assert f"{variable}=" in env_example


def test_production_rejects_mutable_or_misbound_admin_bootstrap():
    prod = {
        "app_env": "production",
        "cookie_secure": True,
        "db_require_tls": True,
        "google_client_id": _GOOGLE_CLIENT_ID,
    }
    with pytest.raises(ValidationError, match="ADMIN_EMAILS is local-only"):
        Settings(**_base(**prod, admin_emails="admin@example.com"))
    with pytest.raises(ValidationError, match="AZURE_ADMIN_TENANT_ID is required"):
        Settings(**_base(**prod, azure_admin_object_ids=_OBJECT_ID))


def test_microsoft_bootstrap_ids_are_validated_and_normalized():
    configured = Settings(
        **_base(
            app_env="production",
            cookie_secure=True,
            db_require_tls=True,
            azure_client_id=_AZURE_CLIENT_ID,
            azure_admin_tenant_id=_TENANT_ID.upper(),
            azure_admin_object_ids=_OBJECT_ID.upper(),
        )
    )
    assert configured.azure_admin_tenant_id == _TENANT_ID
    assert configured.azure_admin_object_id_set == {_OBJECT_ID}

    with pytest.raises(ValidationError, match="must be UUIDs"):
        Settings(**_base(azure_admin_object_ids="object-id-typo"))


def test_oauth_client_ids_are_trimmed_and_azure_client_id_must_be_a_uuid():
    configured = Settings(
        **_base(
            google_client_id=f"  {_GOOGLE_CLIENT_ID}  ",
            azure_client_id=f"  {_AZURE_CLIENT_ID.upper()}  ",
        )
    )
    assert configured.google_client_id == _GOOGLE_CLIENT_ID
    assert configured.azure_client_id == _AZURE_CLIENT_ID

    with pytest.raises(ValidationError, match="AZURE_CLIENT_ID must be a UUID"):
        Settings(**_base(azure_client_id="not-an-application-uuid"))


@pytest.mark.parametrize(
    "client_id",
    [
        "not-a-google-client-id",
        "123456-short.apps.googleusercontent.com",
        f"123456789012-{'x' * 220}.apps.googleusercontent.com",
    ],
)
def test_production_rejects_malformed_google_client_ids(client_id):
    with pytest.raises(ValidationError, match="GOOGLE_CLIENT_ID"):
        Settings(
            **_base(
                app_env="production",
                cookie_secure=True,
                db_require_tls=True,
                google_client_id=client_id,
            )
        )


def test_trusted_proxy_entries_are_explicit_and_canonical():
    configured = Settings(**_base(trusted_proxy_ips=" 10.20.30.40, 192.168.1.44/24, 2001:DB8::1 "))
    assert configured.trusted_proxy_ip_list == [
        "10.20.30.40",
        "192.168.1.0/24",
        "2001:db8::1",
    ]

    for invalid in ("*", "proxy.internal", "10.0.0.1/999"):
        with pytest.raises(ValidationError, match="TRUSTED_PROXY_IPS"):
            Settings(**_base(trusted_proxy_ips=invalid))


@pytest.mark.parametrize("world", ["0.0.0.0/0", "::/0"])
def test_production_rejects_world_wide_proxy_trust(world):
    with pytest.raises(ValidationError, match="TRUSTED_PROXY_IPS"):
        Settings(
            **_base(
                app_env="production",
                cookie_secure=True,
                db_require_tls=True,
                google_client_id=_GOOGLE_CLIENT_ID,
                trusted_proxy_ips=world,
            )
        )


def test_separate_database_fields_safely_encode_reserved_password_characters():
    password = "spaces and @:/?%# are valid"
    configured = Settings(
        **_base(
            database_url="",
            db_host="mysql",
            db_port=3306,
            db_name="appdb",
            db_user="app:user",
            db_password=password,
        )
    )
    parsed = make_url(configured.database_url)
    assert parsed.username == "app:user"
    assert parsed.password == password
    assert parsed.host == "mysql"


def test_database_tls_requires_ca_path():
    with pytest.raises(ValidationError, match="DB_TLS_CA_PATH"):
        Settings(**_base(db_require_tls=True, db_tls_ca_path=" "))


def test_dev_auth_never_enabled_outside_local():
    prod = Settings(
        **_base(
            app_env="production",
            cookie_secure=True,
            db_require_tls=True,
            dev_auth_enabled=True,
            google_client_id=_GOOGLE_CLIENT_ID,
        )
    )
    assert prod.is_dev_auth_enabled is False


def test_dev_auth_flag_without_independent_secret_stays_disabled():
    local = Settings(**_base(app_env="local", dev_auth_enabled=True, dev_auth_secret=""))
    assert local.is_dev_auth_enabled is False


def test_dev_auth_enabled_in_local_with_flag_and_strong_secret():
    local = Settings(
        **_base(
            app_env="local",
            dev_auth_enabled=True,
            dev_auth_secret="independent-dev-secret-0123456789abcdef",
        )
    )
    assert local.is_dev_auth_enabled is True


def test_session_ttl_must_be_positive_and_bounded():
    with pytest.raises(ValidationError):
        Settings(**_base(session_ttl_hours=0))
    with pytest.raises(ValidationError):
        Settings(**_base(session_ttl_hours=169))


def test_rotation_keys_are_strong_unique_and_bounded():
    old = "old-signing-secret-0123456789abcdef0123456789"
    configured = Settings(**_base(session_jwt_previous_secrets=old))
    assert configured.session_jwt_keyring[configured.jwt_key_id(old)] == old

    with pytest.raises(ValidationError):
        Settings(**_base(session_jwt_previous_secrets="weak"))
    with pytest.raises(ValidationError):
        Settings(**_base(session_jwt_previous_secrets=f"{old},{old}"))
    with pytest.raises(ValidationError):
        Settings(**_base(session_jwt_previous_secrets=_GOOD_SECRET))
