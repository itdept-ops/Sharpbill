"""Site settings + signup-approval flow tests."""

from concurrent.futures import ThreadPoolExecutor
from threading import Barrier

import pytest
from fastapi import Request
from sqlalchemy import select

from app.auth import VerifiedIdentity
from app.db import SessionLocal
from app.errors import ApiError
from app.main import app
from app.models import Permission, Role, SiteSettings, User, UserIdentity
from app.routers import settings as settings_router
from app.schemas.settings import SiteSettingsUpdate
from tests.client import TestClient


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


def test_settings_lock_replaces_cached_provider_state_before_transition(client):
    _admin(client)

    with SessionLocal() as stale_db:
        actor = stale_db.scalar(select(User).where(User.email == "admin@example.com"))
        cached = stale_db.get(SiteSettings, 1)
        assert actor is not None and cached is not None
        assert bool(cached.allow_google) and bool(cached.allow_microsoft)

        with SessionLocal() as concurrent_db:
            current = concurrent_db.get(SiteSettings, 1)
            assert current is not None
            current.allow_google = False
            concurrent_db.commit()

        request = Request(
            {
                "type": "http",
                "method": "PUT",
                "path": "/api/admin/settings",
                "headers": [],
                "client": ("127.0.0.1", 12345),
            }
        )
        with pytest.raises(ApiError) as caught:
            settings_router.update_settings(
                body=SiteSettingsUpdate(allow_microsoft=False),
                request=request,
                db=stale_db,
                actor=actor,
            )
        assert caught.value.code == "NO_PROVIDER_ENABLED"
        stale_db.rollback()

    with SessionLocal() as verification_db:
        current = verification_db.get(SiteSettings, 1)
        assert current is not None
        assert bool(current.allow_google) is False
        assert bool(current.allow_microsoft) is True


def test_default_role_transition_refreshes_cached_permission_seniority(client):
    _admin(client)
    delegate_role = client.post(
        "/api/roles",
        json={
            "name": "SettingsRoleRefreshDelegate",
            "permission_keys": ["settings.manage", "roles.manage"],
        },
    ).json()
    target_role = client.post(
        "/api/roles", json={"name": "SettingsRoleRefreshTarget", "permission_keys": []}
    ).json()
    delegate = TestClient(app)
    assert (
        delegate.post(
            "/api/auth/dev",
            json={"email": "settings-role-refresh@example.com", "role": delegate_role["name"]},
        ).status_code
        == 200
    )

    with SessionLocal() as stale_db:
        actor = stale_db.scalar(
            select(User).where(User.email == "settings-role-refresh@example.com")
        )
        cached = stale_db.get(Role, target_role["id"])
        assert actor is not None and cached is not None
        assert cached.permission_keys == set()

        with SessionLocal() as concurrent_db:
            current = concurrent_db.get(Role, target_role["id"])
            users_manage = concurrent_db.scalar(
                select(Permission).where(Permission.key == "users.manage")
            )
            assert current is not None and users_manage is not None
            current.permissions = [*current.permissions, users_manage]
            current.version += 1
            concurrent_db.commit()

        request = Request(
            {
                "type": "http",
                "method": "PUT",
                "path": "/api/admin/settings",
                "headers": [],
                "client": ("127.0.0.1", 12345),
            }
        )
        with pytest.raises(ApiError) as caught:
            settings_router.update_settings(
                body=SiteSettingsUpdate(default_role_id=target_role["id"]),
                request=request,
                db=stale_db,
                actor=actor,
            )
        assert caught.value.code == "INSUFFICIENT_PRIVILEGE"
        stale_db.rollback()


def test_null_provider_toggle_keeps_existing_effective_state(client):
    _admin(client)
    response = client.put("/api/admin/settings", json={"allow_google": None, "calm_mode": True})
    assert response.status_code == 200
    assert response.json()["allow_google"] is True


def test_non_provider_update_succeeds_without_oauth_configuration(client, monkeypatch):
    """Missing OAuth credentials must not block unrelated local administration."""
    from app.config import settings

    _admin(client)
    monkeypatch.setattr(settings, "google_client_id", "")
    monkeypatch.setattr(settings, "azure_client_id", "")

    response = client.put("/api/admin/settings", json={"calm_mode": True})

    assert response.status_code == 200
    assert response.json()["calm_mode"] is True


def test_calm_mode_round_trips_and_surfaces_in_public_config(client):
    _admin(client)
    assert client.get("/api/auth/config").json()["calm"] is False
    r = client.put("/api/admin/settings", json={"calm_mode": True})
    assert r.status_code == 200 and r.json()["calm_mode"] is True
    # It's exposed on the PUBLIC config so every page can apply it (no auth needed).
    assert client.get("/api/auth/config").json()["calm"] is True


def test_public_config_reports_only_effectively_enabled_providers(db, client, monkeypatch):
    from app.config import settings

    site = db.get(SiteSettings, 1)
    site.allow_google = False
    site.allow_microsoft = True
    db.commit()
    monkeypatch.setattr(settings, "google_client_id", "configured-google")
    monkeypatch.setattr(settings, "azure_client_id", "")

    config = client.get("/api/auth/config").json()
    assert config["google"] is False
    assert config["microsoft"] is False
    assert config["google_client_id"] is None
    assert config["microsoft_client_id"] is None


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

    def must_not_verify(_token: str):  # pragma: no cover - the assertion is the boundary
        raise AssertionError("disabled provider performed outbound token verification")

    monkeypatch.setattr(auth_router, "verify_google_id_token", must_not_verify)
    r = TestClient(app).post("/api/auth/google", json={"id_token": "x"})
    assert r.status_code == 403
    assert r.json()["detail"]["code"] == "PROVIDER_DISABLED"


def test_settings_manage_delegate_cannot_default_to_admin(client):
    """Default-role changes require roles.manage before the target role is considered."""
    _admin(client)
    client.post("/api/roles", json={"name": "SiteMgr", "permission_keys": ["settings.manage"]})
    admin_role = next(r for r in client.get("/api/roles").json() if r["name"] == "admin")["id"]

    delegate = TestClient(app)
    delegate.post("/api/auth/dev", json={"email": "sm@example.com", "role": "SiteMgr"})
    resp = delegate.put("/api/admin/settings", json={"default_role_id": admin_role})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"


def test_settings_manage_without_roles_manage_cannot_set_a_benign_default(client):
    _admin(client)
    benign = client.post(
        "/api/roles", json={"name": "BenignSignupRole", "permission_keys": []}
    ).json()
    client.post("/api/roles", json={"name": "SettingsOnly", "permission_keys": ["settings.manage"]})

    delegate = TestClient(app)
    delegate.post(
        "/api/auth/dev", json={"email": "settings-only@example.com", "role": "SettingsOnly"}
    )
    response = delegate.put("/api/admin/settings", json={"default_role_id": benign["id"]})

    assert response.status_code == 403
    assert response.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"
    assert delegate.get("/api/admin/settings").json()["default_role_name"] == "user"


def test_admin_email_bootstraps_even_when_signup_closed(db, monkeypatch):
    """A configured ADMIN_EMAILS identity provisions as admin even in closed mode (FND-006).

    Closed sign-ups must never lock administration out — the admin-email path is the recovery seam.
    """
    from app.config import settings as app_settings
    from app.routers import auth as auth_router

    s = db.get(SiteSettings, 1)
    s.signup_mode = "closed"
    db.commit()
    monkeypatch.setattr(app_settings, "admin_emails", "boss@example.com")
    monkeypatch.setattr(
        auth_router,
        "verify_google_id_token",
        lambda _t: VerifiedIdentity(
            provider="google", subject="boot-1", email="boss@example.com", display_name="Boss"
        ),
    )
    boss = TestClient(app)
    r = boss.post("/api/auth/google", json={"id_token": "x"})
    assert r.status_code == 200, r.text
    assert r.json()["role"] == "admin"

    # A non-admin identity is still blocked by closed mode.
    monkeypatch.setattr(
        auth_router,
        "verify_google_id_token",
        lambda _t: VerifiedIdentity(
            provider="google", subject="rando-1", email="rando@example.com", display_name="Rando"
        ),
    )
    assert TestClient(app).post("/api/auth/google", json={"id_token": "x"}).status_code == 403


def test_cannot_disable_all_providers(client):
    """Disabling both sign-in providers is rejected — it would lock everyone out (FND-007)."""
    _admin(client)
    # Disabling one provider is fine (the other stays enabled).
    assert client.put("/api/admin/settings", json={"allow_google": False}).status_code == 200
    # Disabling the second too (leaving zero enabled) is rejected.
    resp = client.put("/api/admin/settings", json={"allow_microsoft": False})
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "NO_PROVIDER_ENABLED"


def test_cannot_leave_only_an_unconfigured_provider(client, db, monkeypatch):
    from app.config import settings

    _admin(client)
    monkeypatch.setattr(settings, "google_client_id", "configured-google")
    monkeypatch.setattr(settings, "azure_client_id", "")
    site = db.get(SiteSettings, 1)
    assert site.allow_google and site.allow_microsoft

    response = client.put(
        "/api/admin/settings", json={"allow_google": False, "allow_microsoft": True}
    )
    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "NO_PROVIDER_ENABLED"
    db.refresh(site)
    assert bool(site.allow_google) is True


def _set_identity_provider(db, email: str, provider: str) -> None:
    identity = db.scalar(select(UserIdentity).join(User).where(User.email == email))
    assert identity is not None
    identity.provider = provider
    db.commit()


def _disable_dev_and_bootstrap_paths(monkeypatch) -> None:
    from app.config import settings

    monkeypatch.setattr(settings, "dev_auth_enabled", False)
    monkeypatch.setattr(
        settings, "google_client_id", "123456-testclient.apps.googleusercontent.com"
    )
    monkeypatch.setattr(settings, "azure_client_id", "22222222-3333-4444-8555-666666666666")
    monkeypatch.setattr(settings, "google_admin_subjects", "")
    monkeypatch.setattr(settings, "azure_admin_tenant_id", "")
    monkeypatch.setattr(settings, "azure_admin_object_ids", "")


def test_provider_toggle_cannot_strand_the_only_reachable_administrator(client, db, monkeypatch):
    _admin(client)
    _set_identity_provider(db, "admin@example.com", "google")
    _disable_dev_and_bootstrap_paths(monkeypatch)

    response = client.put(
        "/api/admin/settings", json={"allow_google": False, "allow_microsoft": True}
    )

    assert response.status_code == 400
    assert response.json()["detail"]["code"] == "ADMIN_ACCESS_STRANDED"
    db.expire_all()
    site = db.get(SiteSettings, 1)
    assert site is not None and bool(site.allow_google) is True


def test_user_deactivation_cannot_remove_last_provider_reachable_admin(client, db, monkeypatch):
    _admin(client)
    target = TestClient(app)
    assert (
        target.post(
            "/api/auth/dev", json={"email": "microsoft-admin@example.com", "role": "admin"}
        ).status_code
        == 200
    )
    _set_identity_provider(db, "admin@example.com", "google")
    _set_identity_provider(db, "microsoft-admin@example.com", "microsoft")
    _disable_dev_and_bootstrap_paths(monkeypatch)
    site = db.get(SiteSettings, 1)
    assert site is not None
    site.allow_google = False
    site.allow_microsoft = True
    db.commit()

    target_user = db.scalar(select(User).where(User.email == "microsoft-admin@example.com"))
    assert target_user is not None
    target_user_id = target_user.id
    response = client.patch(f"/api/users/{target_user_id}/status", json={"is_active": False})

    assert response.status_code == 409
    assert response.json()["detail"]["code"] == "ADMIN_ACCESS_STRANDED"
    db.expire_all()
    target_user = db.get(User, target_user_id)
    assert target_user is not None and bool(target_user.is_active) is True


def test_concurrent_provider_updates_cannot_disable_both_providers():
    """The singleton row lock closes the two-admin lost-update lockout race."""
    google_admin = TestClient(app)
    microsoft_admin = TestClient(app)
    _admin(google_admin)
    assert (
        microsoft_admin.post(
            "/api/auth/dev",
            json={"email": "second-admin@example.com", "role": "admin"},
        ).status_code
        == 200
    )
    start = Barrier(2)

    def disable_google():
        start.wait()
        return google_admin.put("/api/admin/settings", json={"allow_google": False})

    def disable_microsoft():
        start.wait()
        return microsoft_admin.put("/api/admin/settings", json={"allow_microsoft": False})

    with ThreadPoolExecutor(max_workers=2) as executor:
        responses = list(executor.map(lambda fn: fn(), (disable_google, disable_microsoft)))

    assert sorted(response.status_code for response in responses) == [200, 400]
    rejected = next(response for response in responses if response.status_code == 400)
    assert rejected.json()["detail"]["code"] == "NO_PROVIDER_ENABLED"

    with SessionLocal() as verification_db:
        site = verification_db.get(SiteSettings, 1)
        assert site is not None
        assert (bool(site.allow_google), bool(site.allow_microsoft)) in {
            (True, False),
            (False, True),
        }


def test_analytics(client):
    _admin(client)
    TestClient(app).post("/api/auth/dev", json={"email": "a2@example.com", "role": "user"})
    resp = client.get("/api/dashboard/analytics")
    assert resp.status_code == 200
    body = resp.json()
    assert {"roles", "providers", "signups", "status"} <= set(body.keys())
    assert len(body["signups"]) == 14
    assert body["status"]["total"] >= 2
