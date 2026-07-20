import threading
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime

import pytest
from sqlalchemy import select

from app.auth import VerifiedIdentity
from app.auth.jwt import COOKIE_NAME
from app.auth.service import dev_upsert_user, find_or_create_user
from app.config import settings
from app.db import SessionLocal
from app.errors import ApiError
from app.models import SiteSettings, User
from app.privacy_lifecycle import anonymize_user


def _google(
    email: str, subject: str = "g-sub-1", hosted_domain: str | None = None
) -> VerifiedIdentity:
    return VerifiedIdentity(
        provider="google",
        subject=subject,
        email=email,
        display_name="G User",
        hosted_domain=hosted_domain,
    )


def test_first_google_login_provisions_user(db):
    user = find_or_create_user(db, _google("newperson@example.com"))
    assert user.id is not None
    assert user.role_name == "user"
    assert user.is_active is True
    assert user.auth_providers == ["google"]
    assert "presence.view" in user.permission_keys


def test_admin_bootstrap_from_admin_emails_google(db, monkeypatch):
    monkeypatch.setattr(settings, "admin_emails", "boss@example.com")
    user = find_or_create_user(db, _google("boss@example.com", subject="g-boss"))
    assert user.role_name == "admin"
    assert "roles.manage" in user.permission_keys


def test_production_google_admin_bootstrap_requires_immutable_subject(db, monkeypatch):
    monkeypatch.setattr(settings, "app_env", "production")
    monkeypatch.setattr(settings, "admin_emails", "email-only@example.com")
    monkeypatch.setattr(settings, "google_admin_subjects", "immutable-admin-subject")

    email_only = find_or_create_user(
        db,
        _google(
            "email-only@example.com",
            subject="not-allowlisted",
            hosted_domain="example.com",
        ),
    )
    immutable = find_or_create_user(
        db,
        _google(
            "different@example.com",
            subject="immutable-admin-subject",
            hosted_domain="example.com",
        ),
    )
    assert email_only.role_name == "user"
    assert immutable.role_name == "admin"


def test_microsoft_admin_requires_matching_tenant_and_object_id(db, monkeypatch):
    """Microsoft admin bootstrap keys on the immutable oid within the admin tenant (FND-008).

    The email/UPN claim carries no verified signal, so it must never grant admin on its own.
    """
    monkeypatch.setattr(settings, "azure_admin_tenant_id", "tenant-abc")
    monkeypatch.setattr(settings, "azure_admin_object_ids", "ms-a,ms-b")

    def ms(subject, tenant):
        return VerifiedIdentity(
            provider="microsoft",
            subject=subject,
            email="boss@example.com",  # same email everywhere: it must not drive the decision
            display_name="Boss",
            tenant_id=tenant,
        )

    # Allowlisted oid + right tenant -> admin.
    assert find_or_create_user(db, ms("ms-a", "tenant-abc")).role_name == "admin"
    # Allowlisted oid but WRONG tenant -> not admin.
    assert find_or_create_user(db, ms("ms-b", "other-tenant")).role_name == "user"
    # Right tenant but oid NOT allowlisted -> not admin (email alone never bootstraps).
    assert find_or_create_user(db, ms("ms-c", "tenant-abc")).role_name == "user"


def test_identity_is_keyed_on_subject_not_email(db):
    """The core anti-spoofing property: identity follows the immutable provider id."""
    original = find_or_create_user(db, _google("me@example.com", subject="stable-123"))

    # Same provider subject, DIFFERENT email (user renamed their Google email) -> same account.
    same = find_or_create_user(db, _google("renamed@example.com", subject="stable-123"))
    assert same.id == original.id

    # Same email, DIFFERENT subject (an impostor with a lookalike email) -> a NEW, separate
    # account. It can never take over the original.
    impostor = find_or_create_user(db, _google("me@example.com", subject="impostor-999"))
    assert impostor.id != original.id


def test_same_email_two_providers_two_accounts(db):
    g = find_or_create_user(db, _google("dup@example.com", subject="g-dup"))
    ms = find_or_create_user(
        db,
        VerifiedIdentity(
            provider="microsoft",
            subject="ms-dup",
            email="dup@example.com",
            display_name="Dup",
            tenant_id="tenant-dup",
        ),
    )
    assert g.id != ms.id


def test_microsoft_identity_is_keyed_by_tenant_and_object_id(db):
    def microsoft(tenant: str, email: str) -> VerifiedIdentity:
        return VerifiedIdentity(
            provider="microsoft",
            subject="shared-tenant-scoped-oid",
            email=email,
            display_name="Tenant member",
            tenant_id=tenant,
        )

    first = find_or_create_user(db, microsoft("tenant-one", "one@example.com"))
    second = find_or_create_user(db, microsoft("tenant-two", "two@example.com"))
    same = find_or_create_user(db, microsoft("tenant-one", "renamed@example.com"))

    assert first.id != second.id
    assert same.id == first.id
    assert first.identities[0].provider_namespace == "tenant-one"
    assert second.identities[0].provider_namespace == "tenant-two"


def test_stored_identity_records_the_provider_id(db):
    user = find_or_create_user(db, _google("id@example.com", subject="google-oid-abc"))
    assert user.identities[0].provider == "google"
    assert user.identities[0].provider_subject == "google-oid-abc"


def test_signature_verified_provider_context_does_not_restrict_open_onboarding(db):
    no_domain = find_or_create_user(
        db,
        _google("personal@example.net", subject="no-hd", hosted_domain=None),
    )
    other_domain = find_or_create_user(
        db,
        _google("member@example.org", subject="other-hd", hosted_domain="workspace.example.org"),
    )
    other_tenant = find_or_create_user(
        db,
        VerifiedIdentity(
            provider="microsoft",
            subject="cross-directory-oid",
            email="member@external.example",
            display_name="External member",
            tenant_id="external-directory-tenant",
        ),
    )

    assert no_domain.identities[0].provider_hosted_domain is None
    assert other_domain.identities[0].provider_hosted_domain == "workspace.example.org"
    assert other_tenant.identities[0].provider_tenant_id == "external-directory-tenant"
    assert {no_domain.role_name, other_domain.role_name, other_tenant.role_name} == {"user"}


def test_new_account_admission_serializes_with_closing_onboarding():
    controller = SessionLocal()
    started = threading.Event()

    def attempt_login() -> str:
        with SessionLocal() as login_db:
            started.set()
            try:
                find_or_create_user(
                    login_db,
                    _google("closing-race@example.com", subject="closing-race-subject"),
                )
            except ApiError as exc:
                return exc.code
            return "PROVISIONED"

    try:
        site = controller.scalar(select(SiteSettings).where(SiteSettings.id == 1).with_for_update())
        assert site is not None
        site.signup_mode = "closed"
        controller.flush()

        with ThreadPoolExecutor(max_workers=1) as executor:
            future = executor.submit(attempt_login)
            assert started.wait(timeout=2)
            time.sleep(0.1)
            assert not future.done()
            controller.commit()
            assert future.result(timeout=5) == "SIGNUP_CLOSED"
    finally:
        controller.rollback()
        controller.close()


def test_google_login_via_http_sets_cookie(client, monkeypatch):
    from app.routers import auth as auth_router

    monkeypatch.setattr(
        auth_router,
        "verify_google_id_token",
        lambda _t: _google("httpuser@example.com", subject="g-http"),
    )
    resp = client.post("/api/auth/google", json={"id_token": "whatever"})
    assert resp.status_code == 200
    assert COOKIE_NAME in resp.cookies
    body = resp.json()
    assert body["email"] == "httpuser@example.com"
    assert body["role"] == "user"
    assert body["identities"][0]["subject"] == "g-http"
    assert body["identities"][0]["namespace"] is None


def test_login_rejects_non_json_content_type(client):
    resp = client.post(
        "/api/auth/google", content="id_token=x", headers={"content-type": "text/plain"}
    )
    assert resp.status_code == 415
    assert resp.json()["detail"]["code"] == "UNSUPPORTED_MEDIA_TYPE"


def test_me_requires_session(client):
    resp = client.get("/api/auth/me")
    assert resp.status_code == 401
    assert resp.json()["detail"]["code"] == "NOT_AUTHENTICATED"


def test_disabled_account_cannot_login(db, client, monkeypatch):
    from app.routers import auth as auth_router

    find_or_create_user(db, _google("dis@example.com", subject="g-dis"))
    u = db.query(User).filter(User.email == "dis@example.com").first()
    u.is_active = False
    u.deactivated_at = datetime.now(UTC).replace(tzinfo=None)
    db.commit()

    monkeypatch.setattr(
        auth_router,
        "verify_google_id_token",
        lambda _t: _google("dis@example.com", subject="g-dis"),
    )
    resp = client.post("/api/auth/google", json={"id_token": "x"})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "ACCOUNT_DISABLED"


def test_existing_login_cannot_restore_timestamps_after_concurrent_erasure(db):
    identity = _google("erasure-race@example.com", subject="erasure-race-subject")
    user = find_or_create_user(db, identity)
    stale_login = SessionLocal()
    try:
        stale_user = stale_login.get(User, user.id)
        assert stale_user is not None and stale_user.erased_at is None

        with SessionLocal() as eraser:
            target = eraser.get(User, user.id)
            assert target is not None
            anonymize_user(eraser, target, policy_trigger="login_race_test")
            eraser.commit()

        with pytest.raises(ApiError) as caught:
            find_or_create_user(stale_login, identity)
        assert caught.value.code == "ACCOUNT_ERASED"
        stale_login.rollback()

        erased = stale_login.get(User, user.id)
        assert erased is not None and erased.erased_at is not None
        assert erased.last_login_at is None
        assert erased.last_seen_at is None
    finally:
        stale_login.close()


def test_dev_login_cannot_restore_timestamps_after_concurrent_erasure(db):
    user = dev_upsert_user(db, "dev-erasure-race@example.com", "user", "Original name")
    stale_login = SessionLocal()
    try:
        stale_user = stale_login.get(User, user.id)
        assert stale_user is not None and stale_user.erased_at is None

        with SessionLocal() as eraser:
            target = eraser.get(User, user.id)
            assert target is not None
            anonymize_user(eraser, target, policy_trigger="dev_login_race_test")
            eraser.commit()

        result = dev_upsert_user(
            stale_login,
            "dev-erasure-race@example.com",
            "admin",
            "Restored name",
        )
        assert result.erased_at is not None
        assert result.display_name is None
        assert result.last_login_at is None
        assert result.last_seen_at is None

        stale_login.rollback()
        erased = stale_login.get(User, user.id)
        assert erased is not None
        assert erased.email == f"erased-{user.id}@privacy.invalid"
        assert erased.display_name is None
        assert erased.last_login_at is None
        assert erased.last_seen_at is None
    finally:
        stale_login.close()


def test_logout_clears_cookie(client):
    client.post("/api/auth/dev", json={"email": "who@example.com"})
    resp = client.post("/api/auth/logout")
    assert resp.status_code == 204


def test_logout_revokes_token_durably(client):
    client.post("/api/auth/dev", json={"email": "lo@example.com"})
    token = client.cookies.get("session")
    assert client.get("/api/auth/me").status_code == 200
    client.post("/api/auth/logout")
    # Replay the pre-logout token: it must be rejected server-side, not just cleared client-side.
    client.cookies.set("session", token)
    assert client.get("/api/auth/me").status_code == 401


def test_auth_config_reports_dev_enabled(client):
    resp = client.get("/api/auth/config")
    assert resp.status_code == 200
    config = resp.json()
    assert config["dev"] is True
    assert config["google_client_id"] == settings.google_client_id
    assert config["microsoft_client_id"] == settings.azure_client_id


def test_update_location(client):
    client.post("/api/auth/dev", json={"email": "geo@example.com"})
    r = client.post(
        "/api/auth/location", json={"latitude": 37.7749, "longitude": -122.4194, "accuracy": 12.5}
    )
    assert r.status_code == 204
    me = client.get("/api/auth/me").json()
    assert me["last_latitude"] == 37.7749
    assert me["last_longitude"] == -122.4194
    assert me["last_location_at"] is not None
    # location + timezone are derived from the GPS coordinates (offline reverse-geocode)
    assert me["timezone"] == "America/Los_Angeles"
    assert "San Francisco" in (me["location"] or "")


def test_location_validates_range(client):
    client.post("/api/auth/dev", json={"email": "geo2@example.com"})
    assert (
        client.post("/api/auth/location", json={"latitude": 200, "longitude": 0}).status_code == 422
    )


def test_location_requires_session(client):
    assert (
        client.post("/api/auth/location", json={"latitude": 0, "longitude": 0}).status_code == 401
    )


def test_dev_roles_endpoint_lists_all_roles(client):
    # Create a custom role so the list includes more than the system defaults.
    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    client.post("/api/roles", json={"name": "Analyst", "permission_keys": ["users.read"]})
    roles = client.get("/api/auth/dev/roles").json()
    assert "admin" in roles and "user" in roles and "Analyst" in roles
    assert roles.index("admin") < roles.index("Analyst")  # system roles first
