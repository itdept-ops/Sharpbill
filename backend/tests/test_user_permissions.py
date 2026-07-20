"""Per-user permission grants (RBAC + direct grants)."""

from app.main import app
from tests.client import TestClient


def _login(client, email, role="user"):
    return client.post("/api/auth/dev", json={"email": email, "role": role}).json()


def test_direct_grant_adds_an_effective_permission(client):
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "grantee@example.com", "role": "user"}).json()
    assert target.get("/api/users").status_code == 403  # plain user can't read the directory

    _login(client, "admin@example.com", role="admin")
    resp = client.put(
        f"/api/users/{tu['id']}/permissions",
        json={"permission_keys": ["users.read"], "expected_version": tu["access_version"]},
    )
    assert resp.status_code == 200
    body = resp.json()
    assert "users.read" in body["direct_permissions"]
    assert "users.read" in body["permissions"]  # effective = role ∪ direct
    assert "users.read" not in body["role_permissions"]  # NOT from the 'user' role

    # Read fresh per request → the grant takes effect immediately.
    assert target.get("/api/users").status_code == 200


def test_revoking_direct_grants(client):
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "g2@example.com", "role": "user"}).json()
    _login(client, "admin@example.com", role="admin")

    granted = client.put(
        f"/api/users/{tu['id']}/permissions",
        json={"permission_keys": ["users.read"], "expected_version": tu["access_version"]},
    ).json()
    assert target.get("/api/users").status_code == 200
    client.put(
        f"/api/users/{tu['id']}/permissions",
        json={"permission_keys": [], "expected_version": granted["access_version"]},
    )
    assert target.get("/api/users").status_code == 403


def test_cannot_grant_a_permission_you_do_not_hold(client):
    _login(client, "admin@example.com", role="admin")
    client.post(
        "/api/roles", json={"name": "Mgr", "permission_keys": ["users.read", "users.manage"]}
    )
    victim = TestClient(app)
    vu = victim.post("/api/auth/dev", json={"email": "v@example.com", "role": "user"}).json()
    delegate = TestClient(app)
    delegate.post("/api/auth/dev", json={"email": "d@example.com", "role": "Mgr"})

    resp = delegate.put(
        f"/api/users/{vu['id']}/permissions",
        json={
            "permission_keys": ["settings.manage"],
            "expected_version": vu["access_version"],
        },
    )
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"


def test_unknown_permission_rejected(client):
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "u@example.com", "role": "user"}).json()
    _login(client, "admin@example.com", role="admin")
    resp = client.put(
        f"/api/users/{tu['id']}/permissions",
        json={"permission_keys": ["does.not.exist"], "expected_version": tu["access_version"]},
    )
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "UNKNOWN_PERMISSION"


def test_cannot_set_your_own_permissions(client):
    admin = _login(client, "admin@example.com", role="admin")
    resp = client.put(
        f"/api/users/{admin['id']}/permissions", json={"permission_keys": ["users.read"]}
    )
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "CANNOT_MODIFY_SELF"


def test_direct_grants_require_roles_manage_in_addition_to_users_manage(client):
    admin = client
    _login(admin, "admin@example.com", role="admin")
    admin.post(
        "/api/roles",
        json={"name": "UserLifecycleOnly", "permission_keys": ["users.read", "users.manage"]},
    )
    target = TestClient(app)
    target_user = _login(target, "target-access@example.com")
    delegate = TestClient(app)
    _login(delegate, "lifecycle-delegate@example.com", role="UserLifecycleOnly")

    denied = delegate.put(
        f"/api/users/{target_user['id']}/permissions",
        json={"permission_keys": ["users.read"]},
    )

    assert denied.status_code == 403
    assert denied.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"
    # The split is intentional: lifecycle actions still need only users.manage.
    assert (
        delegate.patch(
            f"/api/users/{target_user['id']}/status", json={"is_active": False}
        ).status_code
        == 200
    )


def test_stale_direct_permission_replacement_is_rejected(client):
    target = TestClient(app)
    user = _login(target, "stale-access@example.com")
    _login(client, "admin@example.com", role="admin")

    first = client.put(
        f"/api/users/{user['id']}/permissions",
        json={"permission_keys": ["users.read"], "expected_version": user["access_version"]},
    )
    assert first.status_code == 200
    stale = client.put(
        f"/api/users/{user['id']}/permissions",
        json={"permission_keys": [], "expected_version": user["access_version"]},
    )
    assert stale.status_code == 409
    assert stale.json()["detail"]["code"] == "STALE_WRITE"
    assert client.get(f"/api/users/{user['id']}").json()["direct_permissions"] == ["users.read"]
