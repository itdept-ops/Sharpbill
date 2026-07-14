"""HTTP-level tests for the admin user-management endpoints, driven through dev login."""


def _login(client, email, role=None):
    body = {"email": email}
    if role:
        body["role"] = role
    resp = client.post("/api/auth/dev", json=body)
    assert resp.status_code == 200, resp.text
    return resp.json()


def test_admin_can_list_users(client):
    _login(client, "admin@example.com", role="admin")
    resp = client.get("/api/users")
    assert resp.status_code == 200
    body = resp.json()
    assert set(body.keys()) == {"items", "total"}
    assert body["total"] == 1
    assert body["items"][0]["email"] == "admin@example.com"


def test_non_admin_forbidden(client):
    _login(client, "plain@example.com", role="user")
    resp = client.get("/api/users")
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "FORBIDDEN"


def test_admin_can_change_another_users_role(client):
    _login(client, "target@example.com", role="user")  # create target
    client.post("/api/auth/logout")
    admin = _login(client, "admin@example.com", role="admin")

    users = client.get("/api/users").json()["items"]
    target = next(u for u in users if u["email"] == "target@example.com")
    assert target["id"] != admin["id"]

    resp = client.patch(f"/api/users/{target['id']}/role", json={"role": "admin"})
    assert resp.status_code == 200
    assert resp.json()["role"] == "admin"


def test_admin_can_deactivate_another_user(client):
    _login(client, "victim@example.com", role="user")
    client.post("/api/auth/logout")
    _login(client, "admin@example.com", role="admin")

    users = client.get("/api/users").json()["items"]
    victim = next(u for u in users if u["email"] == "victim@example.com")

    resp = client.patch(f"/api/users/{victim['id']}/status", json={"is_active": False})
    assert resp.status_code == 200
    assert resp.json()["is_active"] is False


def test_cannot_change_own_role(client):
    admin = _login(client, "admin@example.com", role="admin")
    resp = client.patch(f"/api/users/{admin['id']}/role", json={"role": "user"})
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "CANNOT_MODIFY_SELF"


def test_cannot_deactivate_self(client):
    admin = _login(client, "admin@example.com", role="admin")
    resp = client.patch(f"/api/users/{admin['id']}/status", json={"is_active": False})
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "CANNOT_MODIFY_SELF"


def test_role_update_unknown_user_404(client):
    _login(client, "admin@example.com", role="admin")
    resp = client.patch("/api/users/999999/role", json={"role": "user"})
    assert resp.status_code == 404
    assert resp.json()["detail"]["code"] == "NOT_FOUND"


def test_invalid_role_rejected(client):
    _login(client, "admin@example.com", role="admin")
    resp = client.patch("/api/users/1/role", json={"role": "superuser"})
    assert resp.status_code == 422
