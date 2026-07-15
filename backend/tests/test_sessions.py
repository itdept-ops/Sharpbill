"""Per-device session tracking and revocation."""

from fastapi.testclient import TestClient

from app.main import app


def _login(client, email, role="user"):
    return client.post("/api/auth/dev", json={"email": email, "role": role}).json()


def test_lists_own_session_and_marks_current(client):
    _login(client, "sess@example.com")
    sessions = client.get("/api/auth/sessions").json()
    assert len(sessions) == 1
    assert sessions[0]["current"] is True


def test_revoking_one_device_leaves_the_others(client):
    _login(client, "multi@example.com")  # device 1
    device2 = TestClient(app)
    device2.post("/api/auth/dev", json={"email": "multi@example.com", "role": "user"})  # device 2

    sessions = client.get("/api/auth/sessions").json()
    assert len(sessions) == 2
    other = next(s for s in sessions if not s["current"])  # device 2, from device 1's view

    assert client.delete(f"/api/auth/sessions/{other['id']}").status_code == 204
    assert device2.get("/api/auth/me").status_code == 401  # device 2 signed out
    assert client.get("/api/auth/me").status_code == 200  # device 1 still valid


def test_cannot_revoke_another_users_session(client):
    victim = TestClient(app)
    victim.post("/api/auth/dev", json={"email": "victim-sess@example.com", "role": "user"})
    vsession = victim.get("/api/auth/sessions").json()[0]

    _login(client, "attacker@example.com")
    assert client.delete(f"/api/auth/sessions/{vsession['id']}").status_code == 404
    assert victim.get("/api/auth/me").status_code == 200  # untouched


def test_admin_lists_and_revokes_a_users_session(client):
    target = TestClient(app)
    tu = target.post(
        "/api/auth/dev", json={"email": "target-sess@example.com", "role": "user"}
    ).json()

    _login(client, "admin@example.com", role="admin")
    sessions = client.get(f"/api/users/{tu['id']}/sessions").json()
    assert len(sessions) == 1

    assert client.delete(f"/api/users/{tu['id']}/sessions/{sessions[0]['id']}").status_code == 204
    assert target.get("/api/auth/me").status_code == 401  # signed out on the next request


def test_logout_only_signs_out_the_current_device(client):
    _login(client, "lo@example.com")  # device 1
    device2 = TestClient(app)
    device2.post("/api/auth/dev", json={"email": "lo@example.com", "role": "user"})  # device 2

    assert client.post("/api/auth/logout").status_code == 204
    assert device2.get("/api/auth/me").status_code == 200  # other device stays signed in


def test_kick_revokes_all_of_a_users_sessions(client):
    target = TestClient(app)
    tu = target.post(
        "/api/auth/dev", json={"email": "kick-sess@example.com", "role": "user"}
    ).json()
    device2 = TestClient(app)
    device2.post("/api/auth/dev", json={"email": "kick-sess@example.com", "role": "user"})

    _login(client, "admin@example.com", role="admin")
    assert client.post(f"/api/users/{tu['id']}/kick").status_code == 200

    assert target.get("/api/auth/me").status_code == 401
    assert device2.get("/api/auth/me").status_code == 401  # every device
