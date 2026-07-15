"""Seniority / privilege-hierarchy guards: a non-admin delegate cannot act on principals
who outrank them, and a roles.manage delegate cannot rewrite/delete a role above their
privilege. Covers FND-002 and FND-004."""

from fastapi.testclient import TestClient

from app.main import app


def _login(client, email, role=None):
    body = {"email": email}
    if role:
        body["role"] = role
    r = client.post("/api/auth/dev", json=body)
    assert r.status_code == 200, r.text
    return r.json()


def _role_id(client, name):
    return next(r["id"] for r in client.get("/api/roles").json() if r["name"] == name)


def test_non_admin_delegate_cannot_kick_deactivate_or_demote_admin():
    """A Manager-style role with users.manage + presence.kick still can't touch an admin."""
    admin = TestClient(app)
    a = _login(admin, "admin@example.com", role="admin")
    admin.post(
        "/api/roles",
        json={
            "name": "Manager",
            "permission_keys": ["users.read", "users.manage", "presence.view", "presence.kick"],
        },
    )
    mgr = TestClient(app)
    _login(mgr, "mgr@example.com", role="Manager")

    # Kick an admin -> blocked (kick has no last-admin guard at all; seniority is the protection).
    r = mgr.post(f"/api/users/{a['id']}/kick")
    assert r.status_code == 403
    assert r.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"

    # Deactivate an admin -> blocked.
    assert mgr.patch(f"/api/users/{a['id']}/status", json={"is_active": False}).status_code == 403

    # Demote an admin -> blocked.
    user_role = _role_id(admin, "user")
    assert mgr.patch(f"/api/users/{a['id']}/role", json={"role_id": user_role}).status_code == 403

    # Bulk deactivate an admin -> reported per-item as a privilege failure, not applied.
    bulk = mgr.post("/api/users/bulk", json={"ids": [a["id"]], "action": "deactivate"})
    assert bulk.status_code == 200
    assert bulk.json()["applied"] == 0
    assert bulk.json()["results"][0]["error"] == "INSUFFICIENT_PRIVILEGE"


def test_delegate_can_still_manage_ordinary_members():
    """The guard is scoped to admin targets — normal member management is unaffected."""
    admin = TestClient(app)
    _login(admin, "admin@example.com", role="admin")
    admin.post(
        "/api/roles",
        json={
            "name": "Manager2",
            "permission_keys": ["users.read", "users.manage", "presence.view", "presence.kick"],
        },
    )
    mgr = TestClient(app)
    _login(mgr, "mgr2@example.com", role="Manager2")
    sub = TestClient(app)
    s = _login(sub, "sub@example.com", role="user")

    assert mgr.patch(f"/api/users/{s['id']}/status", json={"is_active": False}).status_code == 200
    assert mgr.post(f"/api/users/{s['id']}/kick").status_code == 200


def test_admin_can_act_on_another_admin():
    """Admins are exempt from the seniority guard (subject to self + last-admin checks)."""
    admin = TestClient(app)
    _login(admin, "admin@example.com", role="admin")
    a2 = (
        TestClient(app)
        .post("/api/auth/dev", json={"email": "admin2@example.com", "role": "admin"})
        .json()
    )
    # Two admins exist, so kicking/deactivating the other is allowed.
    assert admin.post(f"/api/users/{a2['id']}/kick").status_code == 200
    deact = admin.patch(f"/api/users/{a2['id']}/status", json={"is_active": False})
    assert deact.status_code == 200


def test_roles_delegate_cannot_edit_or_delete_role_above_privilege():
    """A roles.manage delegate can't strip/delete a custom role it doesn't fully hold (FND-004)."""
    admin = TestClient(app)
    _login(admin, "admin@example.com", role="admin")
    high = admin.post(
        "/api/roles",
        json={"name": "High", "permission_keys": ["users.manage", "settings.manage"]},
    ).json()["id"]
    admin.post("/api/roles", json={"name": "RoleMgrX", "permission_keys": ["roles.manage"]})

    delegate = TestClient(app)
    _login(delegate, "rm@example.com", role="RoleMgrX")

    # Stripping the higher role's permissions -> blocked.
    r = delegate.patch(f"/api/roles/{high}", json={"permission_keys": []})
    assert r.status_code == 403
    assert r.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"
    # Deleting it -> blocked.
    assert delegate.delete(f"/api/roles/{high}").status_code == 403


def test_roles_delegate_can_manage_role_within_its_privilege():
    """A roles.manage + presence.view delegate can still edit a role built only from perms held."""
    admin = TestClient(app)
    _login(admin, "admin@example.com", role="admin")
    low = admin.post(
        "/api/roles", json={"name": "LowRole", "permission_keys": ["presence.view"]}
    ).json()["id"]
    admin.post(
        "/api/roles",
        json={"name": "RoleMgrY", "permission_keys": ["roles.manage", "presence.view"]},
    )
    delegate = TestClient(app)
    _login(delegate, "rmy@example.com", role="RoleMgrY")

    # The delegate holds presence.view, so it may edit a role that only grants presence.view.
    assert delegate.patch(f"/api/roles/{low}", json={"description": "tweaked"}).status_code == 200
    assert delegate.delete(f"/api/roles/{low}").status_code == 204
