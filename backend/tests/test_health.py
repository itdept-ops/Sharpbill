from collections.abc import Iterator

from app.db import get_db
from app.main import app


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
    }


def test_readiness_requires_the_current_alembic_head(client):
    response = client.get("/api/health/ready")
    assert response.status_code == 200
    assert response.json() == {"status": "ready", "database": "ok", "schema": "ok"}


def test_legacy_health_route_is_the_readiness_alias(client):
    assert client.get("/api/health").json() == client.get("/api/health/ready").json()
