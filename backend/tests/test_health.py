import json
from collections.abc import Iterator

from app.config import settings
from app.db import get_db
from app.main import app
from app.models import Role, SiteSettings, UserIdentity
from app.routers import health


class _UnavailableDatabase:
    def execute(self, *_args, **_kwargs):
        raise ConnectionError("simulated database outage")


def _unavailable_database() -> Iterator[_UnavailableDatabase]:
    yield _UnavailableDatabase()


def test_liveness_is_independent_from_database_and_readiness_fails_closed(client):
    app.dependency_overrides[get_db] = _unavailable_database
    try:
        live = client.get("/api/health/live")
        ready = client.get("/api/health/ready")
    finally:
        app.dependency_overrides.pop(get_db, None)

    assert live.status_code == 200
    assert live.json() == {"status": "alive"}
    assert ready.status_code == 503
    assert ready.json() == {
        "status": "not_ready",
        "database": "error",
        "schema": "unknown",
        "identity_provider": "unknown",
        "administration": "unknown",
        "admission_policy": "unknown",
    }


def test_readiness_requires_the_current_alembic_head(client):
    response = client.get("/api/health/ready")
    assert response.status_code == 200
    assert response.json() == {
        "status": "ready",
        "database": "ok",
        "schema": "ok",
        "identity_provider": "ok",
        "administration": "ok",
        "admission_policy": "ok",
    }


def test_readiness_fails_when_environment_and_site_leave_no_effective_provider(
    client, db, monkeypatch
):
    site = db.get(SiteSettings, 1)
    assert site is not None
    site.allow_google = False
    site.allow_microsoft = True
    db.commit()
    monkeypatch.setattr(settings, "azure_client_id", "")
    monkeypatch.setattr(settings, "app_env", "production")

    response = client.get("/api/health/ready")
    assert response.status_code == 503
    assert response.json() == {
        "status": "not_ready",
        "database": "ok",
        "schema": "ok",
        "identity_provider": "unavailable",
        "administration": "unknown",
        "admission_policy": "ok",
    }


def test_local_dev_auth_is_an_effective_readiness_path(client, monkeypatch):
    monkeypatch.setattr(settings, "google_client_id", "")
    monkeypatch.setattr(settings, "azure_client_id", "")
    response = client.get("/api/health/ready")
    assert response.status_code == 200
    assert response.json()["administration"] == "ok"


def test_fresh_production_requires_an_effective_admin_bootstrap(client, monkeypatch):
    monkeypatch.setattr(settings, "app_env", "production")
    monkeypatch.setattr(settings, "google_admin_subjects", "")
    monkeypatch.setattr(settings, "azure_admin_object_ids", "")
    response = client.get("/api/health/ready")
    assert response.status_code == 503
    assert response.json()["identity_provider"] == "ok"
    assert response.json()["administration"] == "unavailable"


def test_existing_active_admin_keeps_production_ready_without_bootstrap(client, db, monkeypatch):
    created = client.post(
        "/api/auth/dev", json={"email": "existing-admin@example.com", "role": "admin"}
    )
    assert created.status_code == 200
    identity = db.query(UserIdentity).filter(UserIdentity.user_id == created.json()["id"]).one()
    identity.provider = "google"
    identity.provider_subject = "existing-admin-google-subject"
    identity.provider_hosted_domain = "example.com"
    db.commit()
    monkeypatch.setattr(settings, "app_env", "production")
    monkeypatch.setattr(settings, "google_admin_subjects", "")
    monkeypatch.setattr(settings, "azure_admin_object_ids", "")
    response = client.get("/api/health/ready")
    assert response.status_code == 200
    assert response.json()["administration"] == "ok"


def test_admin_on_a_disabled_provider_does_not_satisfy_readiness(client, db, monkeypatch):
    created = client.post(
        "/api/auth/dev", json={"email": "stranded-admin@example.com", "role": "admin"}
    )
    assert created.status_code == 200
    identity = db.query(UserIdentity).filter(UserIdentity.user_id == created.json()["id"]).one()
    identity.provider = "google"
    identity.provider_subject = "stranded-admin-google-subject"
    site = db.get(SiteSettings, 1)
    assert site is not None
    site.allow_google = False
    site.allow_microsoft = True
    db.commit()
    monkeypatch.setattr(settings, "app_env", "production")
    monkeypatch.setattr(settings, "google_admin_subjects", "")
    monkeypatch.setattr(settings, "azure_admin_object_ids", "")
    response = client.get("/api/health/ready")
    assert response.status_code == 503
    assert response.json()["identity_provider"] == "ok"
    assert response.json()["administration"] == "unavailable"


def test_bootstrap_must_match_an_effective_provider(client, db, monkeypatch):
    site = db.get(SiteSettings, 1)
    assert site is not None
    site.allow_google = False
    site.allow_microsoft = True
    db.commit()
    monkeypatch.setattr(settings, "app_env", "production")
    monkeypatch.setattr(settings, "google_admin_subjects", "signed-google-subject")
    monkeypatch.setattr(settings, "azure_admin_object_ids", "")
    response = client.get("/api/health/ready")
    assert response.status_code == 503
    assert response.json()["identity_provider"] == "ok"
    assert response.json()["administration"] == "unavailable"


def test_admin_signup_default_fails_readiness_closed(client, db):
    site = db.get(SiteSettings, 1)
    admin_role = db.query(Role).filter(Role.name == "admin").one()
    assert site is not None
    site.default_role_id = admin_role.id
    db.commit()
    response = client.get("/api/health/ready")
    assert response.status_code == 503
    assert response.json()["admission_policy"] == "unsafe"


def test_legacy_health_route_is_the_readiness_alias(client):
    assert client.get("/api/health").json() == client.get("/api/health/ready").json()


def test_readiness_briefly_caches_database_probe_results(client, monkeypatch):
    calls = 0
    original = health._uncached_readiness

    def counted_readiness(db):
        nonlocal calls
        calls += 1
        return original(db)

    monkeypatch.setattr(health, "_uncached_readiness", counted_readiness)
    assert client.get("/api/health/ready").status_code == 200
    assert client.get("/api/health/ready").status_code == 200
    assert calls == 1


def test_readiness_fails_fast_while_another_probe_is_in_flight(db):
    health._reset_readiness_cache()
    health._readiness_probe_lock.acquire()
    try:
        response = health._readiness(db)
    finally:
        health._readiness_probe_lock.release()

    assert response.status_code == 503
    assert json.loads(response.body)["database"] == "probe_in_progress"
