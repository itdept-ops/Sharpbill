"""Presence (online users) tests."""

from datetime import UTC, datetime

from sqlalchemy import select

from app.main import app
from app.models import User
from app.routers import presence as presence_router
from tests.client import TestClient


def _login(client, email, role=None):
    body = {"email": email}
    if role:
        body["role"] = role
    assert client.post("/api/auth/dev", json=body).status_code == 200


def test_active_user_appears_online(client):
    _login(client, "here@example.com", role="user")  # user role has presence.view
    resp = client.get("/api/presence/online")
    assert resp.status_code == 200
    body = resp.json()
    assert body["count"] >= 1
    assert any(u["display_name"] == "here" for u in body["online"])
    assert all("email" not in u for u in body["online"])  # presence must not leak email
    assert body["window_seconds"] > 0
    assert body["truncated"] is False
    assert body["roster_limit"] > 0


def test_presence_requires_permission(client, db):
    # Build a role with NO presence.view and assign a user to it.
    _login(client, "admin@example.com", role="admin")
    role_id = client.post("/api/roles", json={"name": "Blind", "permission_keys": []}).json()["id"]
    blind = TestClient(app)
    bu = blind.post("/api/auth/dev", json={"email": "blind@example.com", "role": "user"}).json()
    client.patch(
        f"/api/users/{bu['id']}/role",
        json={"role_id": role_id, "expected_version": bu["access_version"]},
    )

    assert blind.get("/api/presence/online").status_code == 403


def test_heartbeat_ok(client):
    _login(client, "beat@example.com", role="user")
    resp = client.post("/api/presence/heartbeat")
    assert resp.status_code == 200
    assert resp.json()["ok"] is True


def test_two_users_both_online(client):
    _login(client, "a@example.com", role="user")
    other = TestClient(app)
    other.post("/api/auth/dev", json={"email": "b@example.com", "role": "user"})

    names = {u["display_name"] for u in client.get("/api/presence/online").json()["online"]}
    assert {"a", "b"} <= names


def test_pending_user_is_excluded_from_presence(client, db):
    pending_client = TestClient(app)
    pending = pending_client.post(
        "/api/auth/dev", json={"email": "pending-presence@example.com", "role": "user"}
    ).json()
    pending_user = db.scalar(select(User).where(User.id == pending["id"]))
    assert pending_user is not None
    pending_user.is_approved = False
    pending_user.last_seen_at = datetime.now(UTC).replace(tzinfo=None)
    db.commit()

    _login(client, "presence-admin@example.com", role="admin")
    online_ids = {item["id"] for item in client.get("/api/presence/online").json()["online"]}
    assert pending["id"] not in online_ids


def test_presence_roster_is_bounded_deterministically(client, db, monkeypatch):
    monkeypatch.setattr(presence_router, "_PRESENCE_ROSTER_LIMIT", 2)
    _login(client, "roster-admin@example.com", role="admin")
    for index in range(3):
        other = TestClient(app)
        _login(other, f"roster-{index}@example.com", role="user")

    # Equal timestamps force the stable primary-key tie-breaker to determine the page.
    seen_at = datetime.now(UTC).replace(tzinfo=None)
    eligible = list(db.scalars(select(User).where(User.is_active.is_(True))))
    for user in eligible:
        user.last_seen_at = seen_at
    db.commit()

    body = client.get("/api/presence/online").json()
    expected_ids = sorted((user.id for user in eligible), reverse=True)
    assert [item["id"] for item in body["online"]] == expected_ids[:2]
    assert body["count"] == len(expected_ids)
    assert body["roster_limit"] == 2
    assert body["truncated"] is True
