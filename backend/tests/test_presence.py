"""Presence (online users) tests."""

from fastapi.testclient import TestClient

from app.main import app


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
    assert any(u["email"] == "here@example.com" for u in body["online"])
    assert body["window_seconds"] > 0


def test_presence_requires_permission(client, db):
    # Build a role with NO presence.view and assign a user to it.
    _login(client, "admin@example.com", role="admin")
    role_id = client.post("/api/roles", json={"name": "Blind", "permission_keys": []}).json()["id"]
    blind = TestClient(app)
    bu = blind.post("/api/auth/dev", json={"email": "blind@example.com", "role": "user"}).json()
    client.patch(f"/api/users/{bu['id']}/role", json={"role_id": role_id})

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

    emails = {u["email"] for u in client.get("/api/presence/online").json()["online"]}
    assert {"a@example.com", "b@example.com"} <= emails
