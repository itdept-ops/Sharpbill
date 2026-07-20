import pytest

from app.auth import VerifiedIdentity
from app.auth.jwt import COOKIE_NAME
from app.auth.service import find_or_create_user
from app.config import settings
from app.errors import ApiError


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
    monkeypatch.setattr(settings, "allowed_email_domains", "example.com")

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
            provider="microsoft", subject="ms-dup", email="dup@example.com", display_name="Dup"
        ),
    )
    assert g.id != ms.id


def test_stored_identity_records_the_provider_id(db):
    user = find_or_create_user(db, _google("id@example.com", subject="google-oid-abc"))
    assert user.identities[0].provider == "google"
    assert user.identities[0].provider_subject == "google-oid-abc"


def test_google_org_allowlist_requires_the_signed_hosted_domain(db, monkeypatch):
    monkeypatch.setattr(settings, "allowed_email_domains", "example.com")

    with pytest.raises(ApiError) as missing:
        find_or_create_user(
            db,
            _google("matching-email@example.com", subject="no-hd", hosted_domain=None),
        )
    assert missing.value.detail["code"] == "LOGIN_NOT_ALLOWED"

    with pytest.raises(ApiError):
        find_or_create_user(
            db,
            _google("matching-email@example.com", subject="wrong-hd", hosted_domain="other.com"),
        )

    accepted = find_or_create_user(
        db,
        _google("alias@unrelated.example", subject="signed-hd", hosted_domain="EXAMPLE.COM"),
    )
    assert accepted.id is not None


def test_new_account_requires_allowlist_or_explicit_public_signup(db, monkeypatch):
    monkeypatch.setattr(settings, "allow_public_signup", False)
    monkeypatch.setattr(settings, "allowed_email_domains", "")

    with pytest.raises(ApiError) as denied:
        find_or_create_user(db, _google("closed-by-default@example.com", subject="restricted"))
    assert denied.value.detail["code"] == "SIGNUP_RESTRICTED"


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
    from app.models import User
    from app.routers import auth as auth_router

    find_or_create_user(db, _google("dis@example.com", subject="g-dis"))
    u = db.query(User).filter(User.email == "dis@example.com").first()
    u.is_active = False
    db.commit()

    monkeypatch.setattr(
        auth_router,
        "verify_google_id_token",
        lambda _t: _google("dis@example.com", subject="g-dis"),
    )
    resp = client.post("/api/auth/google", json={"id_token": "x"})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "ACCOUNT_DISABLED"


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
