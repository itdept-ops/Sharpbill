"""HTTP-level tests for user management, RBAC, presence, and kick — via dev login."""

from fastapi.testclient import TestClient

from app.main import app


def _login(client, email, role=None):
    body = {"email": email}
    if role:
        body["role"] = role
    resp = client.post("/api/auth/dev", json=body)
    assert resp.status_code == 200, resp.text
    return resp.json()


def _role_id(client, name):
    return next(r["id"] for r in client.get("/api/roles").json() if r["name"] == name)


def test_admin_can_list_users(client):
    _login(client, "admin@example.com", role="admin")
    resp = client.get("/api/users")
    assert resp.status_code == 200
    body = resp.json()
    assert set(body.keys()) == {"items", "total"}
    assert body["total"] == 1
    u = body["items"][0]
    assert u["email"] == "admin@example.com"
    assert u["role"] == "admin"
    assert "users.manage" in u["permissions"]
    assert u["identities"][0]["provider"] == "dev"
    assert "subject" in u["identities"][0]
    assert u["online"] is True


def test_regular_user_forbidden_from_user_list(client):
    _login(client, "plain@example.com", role="user")
    resp = client.get("/api/users")
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "FORBIDDEN"


def test_admin_can_change_another_users_role(client):
    _login(client, "target@example.com", role="user")
    client.post("/api/auth/logout")
    admin = _login(client, "admin@example.com", role="admin")
    admin_role = _role_id(client, "admin")

    users = client.get("/api/users").json()["items"]
    target = next(u for u in users if u["email"] == "target@example.com")
    assert target["id"] != admin["id"]

    resp = client.patch(f"/api/users/{target['id']}/role", json={"role_id": admin_role})
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
    user_role = _role_id(client, "user")
    resp = client.patch(f"/api/users/{admin['id']}/role", json={"role_id": user_role})
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "CANNOT_MODIFY_SELF"


def test_cannot_deactivate_self(client):
    admin = _login(client, "admin@example.com", role="admin")
    resp = client.patch(f"/api/users/{admin['id']}/status", json={"is_active": False})
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "CANNOT_MODIFY_SELF"


def test_unknown_role_id_rejected(client):
    _login(client, "t2@example.com", role="user")
    client.post("/api/auth/logout")
    _login(client, "admin@example.com", role="admin")
    target = next(
        u for u in client.get("/api/users").json()["items"] if u["email"] == "t2@example.com"
    )
    resp = client.patch(f"/api/users/{target['id']}/role", json={"role_id": 999999})
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "UNKNOWN_ROLE"


def test_role_update_unknown_user_404(client):
    _login(client, "admin@example.com", role="admin")
    user_role = _role_id(client, "user")
    resp = client.patch("/api/users/999999/role", json={"role_id": user_role})
    assert resp.status_code == 404
    assert resp.json()["detail"]["code"] == "NOT_FOUND"


def test_kick_invalidates_existing_session(client):
    # Target has its own cookie jar.
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "kickme@example.com", "role": "user"}).json()
    assert target.get("/api/auth/me").status_code == 200

    # Admin kicks the target.
    _login(client, "admin@example.com", role="admin")
    resp = client.post(f"/api/users/{tu['id']}/kick")
    assert resp.status_code == 200

    # The target's still-held cookie is now rejected.
    me = target.get("/api/auth/me")
    assert me.status_code == 401
    assert me.json()["detail"]["code"] == "SESSION_REVOKED"
    # (Re-logging in the next second issues a fresh, valid session.)


def test_kick_requires_permission(client):
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "kt@example.com", "role": "user"}).json()
    _login(client, "plain@example.com", role="user")  # no presence.kick
    resp = client.post(f"/api/users/{tu['id']}/kick")
    assert resp.status_code == 403


def test_delegate_cannot_assign_admin_role_puppet(client):
    """A users.manage delegate can't promote another account to full admin."""
    _login(client, "admin@example.com", role="admin")
    client.post(
        "/api/roles",
        json={"name": "UserMgr", "permission_keys": ["users.read", "users.manage", "roles.manage"]},
    )
    admin_role = _role_id(client, "admin")

    puppet = TestClient(app)
    pu = puppet.post("/api/auth/dev", json={"email": "puppet@example.com", "role": "user"}).json()
    delegate = TestClient(app)
    delegate.post("/api/auth/dev", json={"email": "delegate2@example.com", "role": "UserMgr"})

    resp = delegate.patch(f"/api/users/{pu['id']}/role", json={"role_id": admin_role})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"


def test_cannot_deactivate_last_admin(client):
    admin = _login(client, "admin@example.com", role="admin")
    client.post(
        "/api/roles", json={"name": "UserMgr2", "permission_keys": ["users.read", "users.manage"]}
    )
    delegate = TestClient(app)
    delegate.post("/api/auth/dev", json={"email": "dg@example.com", "role": "UserMgr2"})

    resp = delegate.patch(f"/api/users/{admin['id']}/status", json={"is_active": False})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "LAST_ADMIN"


def test_deactivation_revokes_sessions_durably(client):
    victim = TestClient(app)
    vu = victim.post("/api/auth/dev", json={"email": "vic@example.com", "role": "user"}).json()
    assert victim.get("/api/auth/me").status_code == 200

    _login(client, "admin@example.com", role="admin")
    client.patch(f"/api/users/{vu['id']}/status", json={"is_active": False})
    assert victim.get("/api/auth/me").status_code == 401  # deactivated

    client.patch(f"/api/users/{vu['id']}/status", json={"is_active": True})
    # Reactivation must NOT resurrect the pre-deactivation session.
    assert victim.get("/api/auth/me").status_code == 401


def test_user_can_edit_own_profile(client):
    me = _login(client, "self@example.com", role="user")
    resp = client.patch(
        f"/api/users/{me['id']}/profile",
        json={"title": "Field Nurse", "department": "Ops", "location": "Remote"},
    )
    assert resp.status_code == 200
    body = resp.json()
    assert body["title"] == "Field Nurse"
    assert body["department"] == "Ops"


def test_user_cannot_edit_others_profile(client):
    other = TestClient(app)
    ou = other.post("/api/auth/dev", json={"email": "other@example.com", "role": "user"}).json()
    _login(client, "nosy@example.com", role="user")
    resp = client.patch(f"/api/users/{ou['id']}/profile", json={"title": "Hacked"})
    assert resp.status_code == 403


def test_admin_can_view_user_detail(client):
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "detail@example.com", "role": "user"}).json()
    _login(client, "admin@example.com", role="admin")
    resp = client.get(f"/api/users/{tu['id']}")
    assert resp.status_code == 200
    assert resp.json()["email"] == "detail@example.com"


def test_filter_users_by_search_and_role(client):
    for e in ("alice@example.com", "bob@example.com"):
        TestClient(app).post("/api/auth/dev", json={"email": e, "role": "user"})
    _login(client, "admin@example.com", role="admin")
    admin_role = _role_id(client, "admin")

    assert client.get("/api/users", params={"search": "alice"}).json()["total"] == 1
    admins = client.get("/api/users", params={"role_id": admin_role}).json()
    assert all(u["role"] == "admin" for u in admins["items"])
