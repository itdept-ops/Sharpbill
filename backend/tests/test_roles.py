"""RBAC: permissions/roles CRUD, protection guards, and end-to-end permission enforcement."""

from fastapi.testclient import TestClient
from sqlalchemy import select

from app.main import app
from app.models import Permission, Role
from app.permissions import BUILTIN_PERMISSIONS, SYSTEM_ROLES


def _login(client, email, role=None):
    body = {"email": email}
    if role:
        body["role"] = role
    resp = client.post("/api/auth/dev", json=body)
    assert resp.status_code == 200, resp.text
    return resp.json()


def test_seed_matches_permissions_module(db):
    """The migration's static seed must not drift from app.permissions."""
    system_perms = {
        p.key for p in db.scalars(select(Permission).where(Permission.is_system.is_(True)))
    }
    assert system_perms == {k for k, _ in BUILTIN_PERMISSIONS}
    for name, spec in SYSTEM_ROLES.items():
        role = db.scalar(select(Role).where(Role.name == name))
        assert role is not None and role.is_system
        assert role.permission_keys == set(spec["permissions"])


def test_admin_lists_builtin_permissions(client):
    _login(client, "admin@example.com", role="admin")
    perms = client.get("/api/permissions").json()
    keys = {p["key"] for p in perms}
    assert {"users.read", "users.manage", "roles.manage", "presence.view", "presence.kick"} <= keys


def test_regular_user_cannot_manage_rbac(client):
    _login(client, "plain@example.com", role="user")
    assert client.get("/api/permissions").status_code == 403
    assert client.get("/api/roles").status_code == 403
    assert client.post("/api/roles", json={"name": "x", "permission_keys": []}).status_code == 403


def test_create_permission(client):
    _login(client, "admin@example.com", role="admin")
    resp = client.post(
        "/api/permissions", json={"key": "reports.export", "description": "Export reports"}
    )
    assert resp.status_code == 201
    body = resp.json()
    assert body["key"] == "reports.export"
    assert body["is_system"] is False
    assert any(p["key"] == "reports.export" for p in client.get("/api/permissions").json())


def test_create_permission_rejects_bad_key(client):
    _login(client, "admin@example.com", role="admin")
    assert client.post("/api/permissions", json={"key": "NotValid Key"}).status_code == 422


def test_duplicate_permission_conflicts(client):
    _login(client, "admin@example.com", role="admin")
    client.post("/api/permissions", json={"key": "reports.export"})
    resp = client.post("/api/permissions", json={"key": "reports.export"})
    assert resp.status_code == 409


def test_create_role_with_unknown_permission_rejected(client):
    _login(client, "admin@example.com", role="admin")
    resp = client.post("/api/roles", json={"name": "Ghost", "permission_keys": ["does.not.exist"]})
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "UNKNOWN_PERMISSION"


def test_custom_role_grants_access_end_to_end(client):
    """Create a role with users.read, assign a user to it, and confirm they gain access."""
    _login(client, "admin@example.com", role="admin")
    created = client.post(
        "/api/roles",
        json={"name": "Auditor", "description": "Read-only", "permission_keys": ["users.read"]},
    )
    assert created.status_code == 201
    role_id = created.json()["id"]

    # A separate user, initially a plain 'user', is forbidden...
    auditor = TestClient(app)
    au = auditor.post("/api/auth/dev", json={"email": "auditor@example.com", "role": "user"}).json()
    assert auditor.get("/api/users").status_code == 403

    # ...admin reassigns them to Auditor...
    assert client.patch(f"/api/users/{au['id']}/role", json={"role_id": role_id}).status_code == 200

    # ...and now (role read fresh from DB per request) they can read the directory.
    listed = auditor.get("/api/users")
    assert listed.status_code == 200
    assert listed.json()["total"] >= 1


def test_admin_role_is_protected(client):
    _login(client, "admin@example.com", role="admin")
    admin_role = next(r for r in client.get("/api/roles").json() if r["name"] == "admin")
    resp = client.patch(f"/api/roles/{admin_role['id']}", json={"permission_keys": []})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "PROTECTED_ROLE"


def test_system_role_cannot_be_renamed_or_deleted(client):
    _login(client, "admin@example.com", role="admin")
    user_role = next(r for r in client.get("/api/roles").json() if r["name"] == "user")
    assert client.patch(f"/api/roles/{user_role['id']}", json={"name": "member"}).status_code == 403
    assert client.delete(f"/api/roles/{user_role['id']}").status_code == 403


def test_delete_role_in_use_conflicts(client):
    _login(client, "admin@example.com", role="admin")
    role_id = client.post("/api/roles", json={"name": "Temp", "permission_keys": []}).json()["id"]
    holder = TestClient(app)
    hu = holder.post("/api/auth/dev", json={"email": "holder@example.com", "role": "user"}).json()
    client.patch(f"/api/users/{hu['id']}/role", json={"role_id": role_id})

    resp = client.delete(f"/api/roles/{role_id}")
    assert resp.status_code == 409
    assert resp.json()["detail"]["code"] == "ROLE_IN_USE"


def test_delete_unused_custom_role(client):
    _login(client, "admin@example.com", role="admin")
    role_id = client.post("/api/roles", json={"name": "Disposable", "permission_keys": []}).json()[
        "id"
    ]
    assert client.delete(f"/api/roles/{role_id}").status_code == 204
    assert all(r["id"] != role_id for r in client.get("/api/roles").json())


def test_delegate_cannot_grant_permissions_it_lacks(client):
    """A roles.manage delegate can't mint a role carrying permissions it doesn't hold."""
    _login(client, "admin@example.com", role="admin")
    client.post("/api/roles", json={"name": "RoleMgr", "permission_keys": ["roles.manage"]})
    delegate = TestClient(app)
    delegate.post("/api/auth/dev", json={"email": "delegate@example.com", "role": "RoleMgr"})

    resp = delegate.post("/api/roles", json={"name": "Climb", "permission_keys": ["users.manage"]})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"


def test_delegate_cannot_edit_system_user_role(client):
    """The base 'user' role can't be rewritten by a non-admin (would mass-escalate everyone)."""
    _login(client, "admin@example.com", role="admin")
    client.post("/api/roles", json={"name": "RoleMgr2", "permission_keys": ["roles.manage"]})
    delegate = TestClient(app)
    delegate.post("/api/auth/dev", json={"email": "d2@example.com", "role": "RoleMgr2"})

    user_role = next(r for r in delegate.get("/api/roles").json() if r["name"] == "user")
    resp = delegate.patch(
        f"/api/roles/{user_role['id']}", json={"permission_keys": ["users.manage"]}
    )
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "PROTECTED_ROLE"


def test_permission_keys_are_case_normalized(client):
    _login(client, "admin@example.com", role="admin")
    r = client.post("/api/roles", json={"name": "CaseTest", "permission_keys": ["USERS.READ"]})
    assert r.status_code == 201
    assert "users.read" in [p["key"] for p in r.json()["permissions"]]


def test_admin_can_attach_custom_permission_to_role(client):
    """An admin can create a runtime permission and then wire it to a role (FND-005).

    Regression: _guard_grantable used to reject this because a fresh custom permission is on
    no role, so it is in nobody's effective set — including the admin's.
    """
    _login(client, "admin@example.com", role="admin")
    assert client.post("/api/permissions", json={"key": "reports.export"}).status_code == 201
    r = client.post("/api/roles", json={"name": "Reporter", "permission_keys": ["reports.export"]})
    assert r.status_code == 201, r.text
    assert "reports.export" in [p["key"] for p in r.json()["permissions"]]
    # And an admin can attach it to an existing custom role via PATCH too.
    rid = client.post("/api/roles", json={"name": "Reporter2", "permission_keys": []}).json()["id"]
    patched = client.patch(f"/api/roles/{rid}", json={"permission_keys": ["reports.export"]})
    assert patched.status_code == 200, patched.text
