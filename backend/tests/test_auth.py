from app.auth import VerifiedIdentity
from app.auth.jwt import COOKIE_NAME
from app.auth.service import find_or_create_user
from app.config import settings


def _google(email: str, subject: str = "g-sub-1") -> VerifiedIdentity:
    return VerifiedIdentity(provider="google", subject=subject, email=email, display_name="G User")


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


def test_microsoft_admin_requires_matching_tenant(db, monkeypatch):
    monkeypatch.setattr(settings, "admin_emails", "boss@example.com")
    monkeypatch.setattr(settings, "azure_admin_tenant_id", "tenant-abc")
    wrong = VerifiedIdentity(
        provider="microsoft",
        subject="ms-1",
        email="boss@example.com",
        display_name="Boss",
        tenant_id="other-tenant",
    )
    assert find_or_create_user(db, wrong).role_name == "user"
    right = VerifiedIdentity(
        provider="microsoft",
        subject="ms-2",
        email="boss@example.com",
        display_name="Boss",
        tenant_id="tenant-abc",
    )
    assert find_or_create_user(db, right).role_name == "admin"


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
        "/api/auth/google", data="id_token=x", headers={"content-type": "text/plain"}
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
    assert resp.json()["dev"] is True
