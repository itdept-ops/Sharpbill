"""Site settings + signup-approval flow tests."""

from fastapi.testclient import TestClient

from app.auth import VerifiedIdentity
from app.main import app
from app.models import SiteSettings


def _admin(client):
    assert (
        client.post(
            "/api/auth/dev", json={"email": "admin@example.com", "role": "admin"}
        ).status_code
        == 200
    )


def test_read_settings(client):
    _admin(client)
    resp = client.get("/api/admin/settings")
    assert resp.status_code == 200
    body = resp.json()
    assert body["signup_mode"] == "open"
    assert body["default_role_name"] == "user"


def test_settings_require_permission(client):
    client.post("/api/auth/dev", json={"email": "plain@example.com", "role": "user"})
    assert client.get("/api/admin/settings").status_code == 403


def test_update_settings(client):
    _admin(client)
    resp = client.put(
        "/api/admin/settings", json={"signup_mode": "approval", "allow_google": False}
    )
    assert resp.status_code == 200
    assert resp.json()["signup_mode"] == "approval"
    assert resp.json()["allow_google"] is False


def test_calm_mode_round_trips_and_surfaces_in_public_config(client):
    _admin(client)
    assert client.get("/api/auth/config").json()["calm"] is False
    r = client.put("/api/admin/settings", json={"calm_mode": True})
    assert r.status_code == 200 and r.json()["calm_mode"] is True
    # It's exposed on the PUBLIC config so every page can apply it (no auth needed).
    assert client.get("/api/auth/config").json()["calm"] is True


def test_signup_approval_flow(db, monkeypatch):
    from app.routers import auth as auth_router

    s = db.get(SiteSettings, 1)
    s.signup_mode = "approval"
    db.commit()

    monkeypatch.setattr(
        auth_router,
        "verify_google_id_token",
        lambda _t: VerifiedIdentity(
            provider="google", subject="ap-1", email="newbie@example.com", display_name="Newbie"
        ),
    )

    newbie = TestClient(app)
    r = newbie.post("/api/auth/google", json={"id_token": "x"})
    assert r.status_code == 403
    assert r.json()["detail"]["code"] == "PENDING_APPROVAL"

    admin = TestClient(app)
    admin.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    pending = admin.get("/api/users", params={"status": "pending"}).json()["items"]
    assert any(u["email"] == "newbie@example.com" and u["status"] == "pending" for u in pending)
    nid = next(u["id"] for u in pending if u["email"] == "newbie@example.com")
    assert admin.post(f"/api/users/{nid}/approve").status_code == 200

    # Now the newbie can sign in.
    assert newbie.post("/api/auth/google", json={"id_token": "x"}).status_code == 200


def test_signup_closed_blocks_new_accounts(db, monkeypatch):
    from app.routers import auth as auth_router

    s = db.get(SiteSettings, 1)
    s.signup_mode = "closed"
    db.commit()
    monkeypatch.setattr(
        auth_router,
        "verify_google_id_token",
        lambda _t: VerifiedIdentity(
            provider="google", subject="cl-1", email="nope@example.com", display_name="Nope"
        ),
    )
    r = TestClient(app).post("/api/auth/google", json={"id_token": "x"})
    assert r.status_code == 403
    assert r.json()["detail"]["code"] == "SIGNUP_CLOSED"


def test_disabled_provider_blocks_login(db, monkeypatch):
    from app.routers import auth as auth_router

    s = db.get(SiteSettings, 1)
    s.allow_google = False
    db.commit()
    monkeypatch.setattr(
        auth_router,
        "verify_google_id_token",
        lambda _t: VerifiedIdentity(
            provider="google", subject="pd-1", email="x@example.com", display_name="X"
        ),
    )
    r = TestClient(app).post("/api/auth/google", json={"id_token": "x"})
    assert r.status_code == 403
    assert r.json()["detail"]["code"] == "PROVIDER_DISABLED"


def test_settings_manage_delegate_cannot_default_to_admin(client):
    """A settings.manage delegate can't set the default signup role to admin (escalation)."""
    _admin(client)
    client.post("/api/roles", json={"name": "SiteMgr", "permission_keys": ["settings.manage"]})
    admin_role = next(r for r in client.get("/api/roles").json() if r["name"] == "admin")["id"]

    delegate = TestClient(app)
    delegate.post("/api/auth/dev", json={"email": "sm@example.com", "role": "SiteMgr"})
    resp = delegate.put("/api/admin/settings", json={"default_role_id": admin_role})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"


def test_analytics(client):
    _admin(client)
    TestClient(app).post("/api/auth/dev", json={"email": "a2@example.com", "role": "user"})
    resp = client.get("/api/dashboard/analytics")
    assert resp.status_code == 200
    body = resp.json()
    assert {"roles", "providers", "signups", "status"} <= set(body.keys())
    assert len(body["signups"]) == 14
    assert body["status"]["total"] >= 2
