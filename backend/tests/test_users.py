"""HTTP-level tests for user management, RBAC, presence, and kick — via dev login."""

import pytest
from sqlalchemy import select
from starlette.requests import Request

from app.account_lifecycle import lock_current_user, refresh_locked_user_access
from app.db import SessionLocal
from app.errors import ApiError
from app.main import app
from app.models import Permission, Role, SecurityEvent, User
from app.privacy_lifecycle import anonymize_user
from app.routers import users as users_router
from app.schemas.user import StatusUpdateRequest
from tests.client import TestClient


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

    resp = client.patch(
        f"/api/users/{target['id']}/role",
        json={"role_id": admin_role, "expected_version": target["access_version"]},
    )
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


def test_cannot_kick_self(client):
    admin = _login(client, "admin@example.com", role="admin")
    resp = client.post(f"/api/users/{admin['id']}/kick")
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "CANNOT_MODIFY_SELF"


def test_search_escapes_like_wildcards(client):
    for e in ("a_b@example.com", "axb@example.com"):
        TestClient(app).post("/api/auth/dev", json={"email": e, "role": "user"})
    _login(client, "admin@example.com", role="admin")
    items = client.get("/api/users", params={"search": "a_b"}).json()["items"]
    emails = {u["email"] for u in items}
    assert "a_b@example.com" in emails
    assert "axb@example.com" not in emails  # '_' must match literally, not as a wildcard


def test_directory_scan_inputs_and_legacy_offset_are_bounded(client):
    _login(client, "admin@example.com", role="admin")
    assert client.get("/api/users", params={"search": "a"}).status_code == 422
    assert client.get("/api/users", params={"offset": 10_001}).status_code == 422


def test_kick_requires_permission(client):
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "kt@example.com", "role": "user"}).json()
    _login(client, "plain@example.com", role="user")  # no presence.kick
    resp = client.post(f"/api/users/{tu['id']}/kick")
    assert resp.status_code == 403


def test_bulk_approve_and_self_skip(client):
    admin = _login(client, "admin@example.com", role="admin")
    ids = []
    for e in ("b1@example.com", "b2@example.com"):
        u = TestClient(app).post("/api/auth/dev", json={"email": e, "role": "user"}).json()
        ids.append(u["id"])
    resp = client.post("/api/users/bulk", json={"ids": [*ids, admin["id"]], "action": "approve"})
    assert resp.status_code == 200
    body = resp.json()
    assert body["applied"] == 2
    self_result = next(r for r in body["results"] if r["id"] == admin["id"])
    assert self_result["ok"] is False and self_result["error"] == "CANNOT_MODIFY_SELF"


def test_bulk_assign_role(client):
    _login(client, "admin@example.com", role="admin")
    admin_role = _role_id(client, "admin")
    u = (
        TestClient(app)
        .post("/api/auth/dev", json={"email": "promote@example.com", "role": "user"})
        .json()
    )
    resp = client.post(
        "/api/users/bulk", json={"ids": [u["id"]], "action": "assign_role", "role_id": admin_role}
    )
    assert resp.status_code == 200
    assert resp.json()["applied"] == 1


def test_bulk_rejects_duplicate_ids_before_any_mutation(client):
    target_client = TestClient(app)
    target = _login(target_client, "bulk-duplicate-target@example.com")
    _login(client, "bulk-duplicate-admin@example.com", role="admin")

    response = client.post(
        "/api/users/bulk",
        json={"ids": [target["id"], target["id"]], "action": "deactivate"},
    )
    assert response.status_code == 422
    with SessionLocal() as verifier:
        stored = verifier.get(User, target["id"])
        assert stored is not None and stored.is_active


def test_bulk_reauthorizes_current_role_permissions_after_each_commit(client, monkeypatch):
    _login(client, "bulk-refresh-admin@example.com", role="admin")
    created_role = client.post(
        "/api/roles",
        json={"name": "BulkLifecycleManager", "permission_keys": ["users.manage"]},
    )
    assert created_role.status_code == 201, created_role.text

    delegate = TestClient(app)
    _login(delegate, "bulk-refresh-delegate@example.com", role="BulkLifecycleManager")
    first_client = TestClient(app)
    second_client = TestClient(app)
    first = _login(first_client, "bulk-refresh-first@example.com")
    second = _login(second_client, "bulk-refresh-second@example.com")

    original = users_router._apply_bulk_user
    applied = 0

    def apply_then_revoke_role_permission(*args, **kwargs):
        nonlocal applied
        result = original(*args, **kwargs)
        applied += 1
        if applied == 1:
            with SessionLocal() as concurrent:
                role = concurrent.scalar(select(Role).where(Role.name == "BulkLifecycleManager"))
                assert role is not None
                role.permissions = [p for p in role.permissions if p.key != "users.manage"]
                role.version += 1
                concurrent.commit()
        return result

    monkeypatch.setattr(users_router, "_apply_bulk_user", apply_then_revoke_role_permission)
    response = delegate.post(
        "/api/users/bulk",
        json={"ids": [first["id"], second["id"]], "action": "deactivate"},
    )
    assert response.status_code == 200, response.text
    assert response.json()["results"] == [
        {"id": first["id"], "ok": True},
        {"id": second["id"], "ok": False, "error": "FORBIDDEN"},
    ]
    with SessionLocal() as verifier:
        first_row = verifier.get(User, first["id"])
        second_row = verifier.get(User, second["id"])
        assert first_row is not None and not first_row.is_active
        assert second_row is not None and second_row.is_active


def test_current_access_refresh_replaces_stale_direct_grants(client):
    user = _login(client, "direct-grant-refresh@example.com")
    with SessionLocal() as setup:
        target = setup.get(User, user["id"])
        permission = setup.scalar(select(Permission).where(Permission.key == "users.manage"))
        assert target is not None and permission is not None
        target.granted_permissions = [permission]
        target.access_version += 1
        setup.commit()

    stale = SessionLocal()
    try:
        cached = stale.get(User, user["id"])
        assert cached is not None and "users.manage" in cached.permission_keys

        with SessionLocal() as concurrent:
            current = concurrent.get(User, user["id"])
            assert current is not None
            current.granted_permissions = []
            current.access_version += 1
            concurrent.commit()

        locked = lock_current_user(stale, user["id"])
        assert locked is not None
        assert refresh_locked_user_access(stale, locked) is not None
        assert "users.manage" not in locked.permission_keys
        stale.rollback()
    finally:
        stale.close()


def test_stale_status_and_approval_mutations_cannot_restore_erased_account(client):
    admin = _login(client, "lifecycle-race-admin@example.com", role="admin")
    target_client = TestClient(app)
    target = _login(target_client, "lifecycle-race-target@example.com")
    status_db = SessionLocal()
    approval_db = SessionLocal()
    try:
        status_actor = status_db.get(User, admin["id"])
        status_target = status_db.get(User, target["id"])
        approval_actor = approval_db.get(User, admin["id"])
        approval_target = approval_db.get(User, target["id"])
        assert status_actor is not None and status_target is not None
        assert approval_actor is not None and approval_target is not None

        with SessionLocal() as eraser:
            current_target = eraser.get(User, target["id"])
            assert current_target is not None
            anonymize_user(eraser, current_target, policy_trigger="admin_mutation_race_test")
            eraser.commit()

        request = Request(
            {
                "type": "http",
                "method": "PATCH",
                "path": f"/api/users/{target['id']}/status",
                "headers": [],
                "client": ("127.0.0.1", 12345),
            }
        )
        with pytest.raises(ApiError) as status_error:
            users_router.update_status(
                target["id"],
                StatusUpdateRequest(is_active=True),
                request,
                db=status_db,
                actor=status_actor,
            )
        assert status_error.value.code == "ACCOUNT_ERASED"
        status_db.rollback()

        with pytest.raises(ApiError) as approval_error:
            users_router.approve_user(target["id"], request, db=approval_db, actor=approval_actor)
        assert approval_error.value.code == "ACCOUNT_ERASED"

        with SessionLocal() as verifier:
            erased = verifier.get(User, target["id"])
            assert erased is not None and erased.erased_at is not None
            assert not erased.is_active
            assert not erased.is_approved
    finally:
        status_db.close()
        approval_db.close()


def test_role_assignment_requires_roles_manage_for_single_and_bulk(client):
    _login(client, "admin@example.com", role="admin")
    client.post(
        "/api/roles",
        json={"name": "LifecycleManager", "permission_keys": ["users.read", "users.manage"]},
    )
    user_role = _role_id(client, "user")
    target = TestClient(app)
    target_user = _login(target, "role-target@example.com", role="user")
    delegate = TestClient(app)
    _login(delegate, "role-delegate@example.com", role="LifecycleManager")

    single = delegate.patch(f"/api/users/{target_user['id']}/role", json={"role_id": user_role})
    bulk = delegate.post(
        "/api/users/bulk",
        json={"ids": [target_user["id"]], "action": "assign_role", "role_id": user_role},
    )

    assert single.status_code == bulk.status_code == 403
    assert single.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"
    assert bulk.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"


def test_scoped_delegate_with_both_authorities_can_assign_only_held_access(client):
    _login(client, "admin@example.com", role="admin")
    client.post(
        "/api/roles",
        json={
            "name": "ScopedAccessDelegate",
            "permission_keys": ["users.read", "users.manage", "roles.manage", "presence.view"],
        },
    )
    limited = client.post(
        "/api/roles",
        json={"name": "LimitedReader", "permission_keys": ["users.read", "presence.view"]},
    ).json()
    target = TestClient(app)
    target_user = _login(target, "scoped-target@example.com")
    delegate = TestClient(app)
    _login(delegate, "scoped-delegate@example.com", role="ScopedAccessDelegate")

    assigned = delegate.patch(
        f"/api/users/{target_user['id']}/role",
        json={"role_id": limited["id"], "expected_version": target_user["access_version"]},
    )
    granted = delegate.put(
        f"/api/users/{target_user['id']}/permissions",
        json={
            "permission_keys": ["users.read"],
            "expected_version": assigned.json()["access_version"],
        },
    )

    assert assigned.status_code == granted.status_code == 200
    assert assigned.json()["role"] == "LimitedReader"
    assert granted.json()["direct_permissions"] == ["users.read"]


def test_provider_subjects_are_visible_only_to_self_or_admin(client):
    _login(client, "admin@example.com", role="admin")
    client.post("/api/roles", json={"name": "DirectoryViewer", "permission_keys": ["users.read"]})
    target = TestClient(app)
    target_user = _login(target, "identity-target@example.com")
    viewer = TestClient(app)
    viewer_user = _login(viewer, "identity-viewer@example.com", role="DirectoryViewer")

    directory = viewer.get("/api/users").json()["items"]
    target_row = next(row for row in directory if row["id"] == target_user["id"])
    self_row = next(row for row in directory if row["id"] == viewer_user["id"])

    assert target_row["identities"] == []
    assert self_row["identities"][0]["subject"] == "identity-viewer@example.com"
    assert viewer.get(f"/api/users/{target_user['id']}").json()["identities"] == []
    admin_row = client.get(f"/api/users/{target_user['id']}").json()
    assert admin_row["identities"][0]["subject"] == "identity-target@example.com"


def test_export_csv_is_audited(client, db):
    admin = _login(client, "admin@example.com", role="admin")
    resp = client.get("/api/users/export.csv")
    assert resp.status_code == 200
    assert resp.headers["content-type"].startswith("text/csv")
    assert "admin@example.com" in resp.text
    assert resp.text.splitlines()[0].startswith("id,email,display_name,role,status")
    event = db.scalar(select(SecurityEvent).where(SecurityEvent.event_type == "users.exported"))
    assert event is not None
    assert event.actor_user_id == admin["id"]
    assert event.target_type == "user_collection"
    assert event.event_metadata == {"exported_count": 1, "filters_applied": False}


def test_user_csv_requires_users_export_independently_of_users_read(client):
    _login(client, "admin@example.com", role="admin")
    assert (
        client.post(
            "/api/roles", json={"name": "DirectoryReader", "permission_keys": ["users.read"]}
        ).status_code
        == 201
    )
    assert (
        client.post(
            "/api/roles", json={"name": "CsvExporter", "permission_keys": ["users.export"]}
        ).status_code
        == 201
    )
    reader = TestClient(app)
    _login(reader, "directory-reader@example.com", role="DirectoryReader")

    assert reader.get("/api/users").status_code == 200
    denied = reader.get("/api/users/export.csv")
    assert denied.status_code == 403
    assert denied.json()["detail"]["code"] == "FORBIDDEN"

    exporter = TestClient(app)
    _login(exporter, "csv-exporter@example.com", role="CsvExporter")
    assert exporter.get("/api/users").status_code == 403
    assert exporter.get("/api/users/export.csv").status_code == 200


def test_user_csv_export_has_a_hard_row_cap(client, monkeypatch):
    from app.routers import users as users_router

    _login(TestClient(app), "extra-user@example.com", role="user")
    _login(client, "admin@example.com", role="admin")
    monkeypatch.setattr(users_router, "_MAX_USER_EXPORT_ROWS", 1)

    response = client.get("/api/users/export.csv")

    assert response.status_code == 413
    assert response.json()["detail"]["code"] == "EXPORT_TOO_LARGE"
    assert "1 rows" in response.json()["detail"]["message"]


def test_csv_export_neutralizes_formula(client):
    victim = TestClient(app)
    vu = victim.post("/api/auth/dev", json={"email": "pwn@example.com", "role": "user"}).json()
    victim.patch(f"/api/users/{vu['id']}/profile", json={"display_name": "=HYPERLINK(1)"})
    _login(client, "admin@example.com", role="admin")
    text = client.get("/api/users/export.csv").text
    assert "'=HYPERLINK(1)" in text  # neutralized with a leading quote
    assert ",=HYPERLINK(1)" not in text  # never a bare formula at cell start


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


def test_delegate_cannot_deactivate_admin(client):
    """A non-admin users.manage delegate cannot deactivate an admin (seniority guard, FND-002).

    Previously this only failed because the admin happened to be the *last* one; now acting on
    any admin as a non-admin is refused outright.
    """
    admin = _login(client, "admin@example.com", role="admin")
    client.post(
        "/api/roles", json={"name": "UserMgr2", "permission_keys": ["users.read", "users.manage"]}
    )
    delegate = TestClient(app)
    delegate.post("/api/auth/dev", json={"email": "dg@example.com", "role": "UserMgr2"})

    resp = delegate.patch(f"/api/users/{admin['id']}/status", json={"is_active": False})
    assert resp.status_code == 403
    assert resp.json()["detail"]["code"] == "INSUFFICIENT_PRIVILEGE"


def test_last_admin_guard_blocks_removing_final_admin(client):
    """The last-admin guard still fires for an admin actor when one active admin remains."""
    _login(client, "admin@example.com", role="admin")
    second = (
        TestClient(app)
        .post("/api/auth/dev", json={"email": "second@example.com", "role": "admin"})
        .json()
    )
    # Deactivate the second admin (two admins exist, so this is allowed) -> one active admin left.
    assert (
        client.patch(f"/api/users/{second['id']}/status", json={"is_active": False}).status_code
        == 200
    )
    user_role = _role_id(client, "user")
    # Demoting the remaining admin-role account would leave zero admins -> LAST_ADMIN.
    resp = client.patch(
        f"/api/users/{second['id']}/role",
        json={"role_id": user_role, "expected_version": second["access_version"]},
    )
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


def test_user_can_set_and_clear_accent_color(client):
    me = _login(client, "styler@example.com", role="user")
    r = client.patch(f"/api/users/{me['id']}/profile", json={"accent_color": "#19E5D0"})
    assert r.status_code == 200
    assert r.json()["accent_color"] == "#19E5D0"
    # a bad hex is rejected by validation
    assert (
        client.patch(f"/api/users/{me['id']}/profile", json={"accent_color": "blue"}).status_code
        == 422
    )
    # null clears it back to the default
    assert (
        client.patch(f"/api/users/{me['id']}/profile", json={"accent_color": None}).json()[
            "accent_color"
        ]
        is None
    )


def test_user_can_merge_and_reset_ui_prefs(client):
    me = _login(client, "prefs@example.com", role="user")
    uid = me["id"]

    def patch(prefs):
        return client.patch(f"/api/users/{uid}/profile", json={"ui_prefs": prefs})

    # a single-key PATCH stores that key
    r = patch({"glow_intensity": "off"})
    assert r.status_code == 200
    assert r.json()["ui_prefs"] == {"glow_intensity": "off"}
    # a second single-key PATCH MERGES (does not replace) the bag
    assert patch({"density": "compact"}).json()["ui_prefs"] == {
        "glow_intensity": "off",
        "density": "compact",
    }
    # overwriting an existing key updates just that key, keeping the rest
    assert patch({"glow_intensity": "intense"}).json()["ui_prefs"] == {
        "glow_intensity": "intense",
        "density": "compact",
    }
    # explicit null resets every axis back to defaults
    assert patch(None).json()["ui_prefs"] is None


def test_ui_prefs_rejects_invalid_values(client):
    me = _login(client, "badprefs@example.com", role="user")
    uid = me["id"]

    def status(prefs):
        return client.patch(f"/api/users/{uid}/profile", json={"ui_prefs": prefs}).status_code

    assert status({"motion": "hyper"}) == 422  # not a valid enum member
    assert status({"bogus": "x"}) == 422  # unknown key (extra='forbid')
    assert status({"rain_density": 5}) == 422  # outside [0, 0.8]
    # accent_color and ui_prefs coexist in one PATCH without interfering
    r = client.patch(
        f"/api/users/{uid}/profile",
        json={"accent_color": "#a3e635", "ui_prefs": {"scanlines": "heavy"}},
    )
    assert r.status_code == 200
    assert r.json()["accent_color"] == "#a3e635"
    assert r.json()["ui_prefs"] == {"scanlines": "heavy"}


def test_profile_update_rejects_unknown_field(client):
    """FND-032: mutating schemas reject unknown fields (mass-assignment guardrail)."""
    me = _login(client, "strict@example.com", role="user")
    r = client.patch(
        f"/api/users/{me['id']}/profile", json={"title": "ok", "role_id": 1, "is_admin": True}
    )
    assert r.status_code == 422
    assert r.json()["detail"]["code"] == "VALIDATION_ERROR"


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
    assert client.get("/api/users", params={"search": "ALICE"}).json()["total"] == 1
    admins = client.get("/api/users", params={"role_id": admin_role}).json()
    assert all(u["role"] == "admin" for u in admins["items"])


def test_location_visible_to_self_and_managers_only(client):
    """Opt-in GPS is only exposed to a user viewing themselves or to users.manage holders."""
    owner = TestClient(app)
    ou = owner.post("/api/auth/dev", json={"email": "loc@example.com", "role": "user"}).json()
    assert (
        owner.post(
            "/api/auth/location", json={"latitude": 40.7, "longitude": -74.0, "accuracy": 10}
        ).status_code
        == 204
    )

    # Self sees their own coordinates.
    assert owner.get(f"/api/users/{ou['id']}").json()["last_latitude"] == 40.7

    # A manager (users.manage) sees everyone's coordinates.
    _login(client, "admin@example.com", role="admin")
    admin_row = next(
        u for u in client.get("/api/users").json()["items"] if u["email"] == "loc@example.com"
    )
    assert admin_row["last_latitude"] == 40.7

    # A users.read-only viewer must NOT see other users' coordinates (list or detail).
    client.post("/api/roles", json={"name": "ReadOnly", "permission_keys": ["users.read"]})
    viewer = TestClient(app)
    viewer.post("/api/auth/dev", json={"email": "ro@example.com", "role": "ReadOnly"})
    v_row = next(
        u for u in viewer.get("/api/users").json()["items"] if u["email"] == "loc@example.com"
    )
    assert v_row["last_latitude"] is None
    assert viewer.get(f"/api/users/{ou['id']}").json()["last_latitude"] is None


def test_derived_location_hidden_from_read_only_viewer(client):
    """FND-012: GPS-derived location + timezone follow the same privacy gate as raw coordinates."""
    owner = TestClient(app)
    ou = owner.post("/api/auth/dev", json={"email": "loc2@example.com", "role": "user"}).json()
    owner.post("/api/auth/location", json={"latitude": 37.7749, "longitude": -122.4194})

    # Self sees their own derived location + timezone.
    self_view = owner.get(f"/api/users/{ou['id']}").json()
    assert self_view["location"] and self_view["timezone"]

    # A manager (users.manage) sees them too.
    _login(client, "admin@example.com", role="admin")
    admin_row = next(
        u for u in client.get("/api/users").json()["items"] if u["email"] == "loc2@example.com"
    )
    assert admin_row["location"] and admin_row["timezone"]

    # A users.read-only viewer does NOT.
    client.post("/api/roles", json={"name": "ReadOnly2", "permission_keys": ["users.read"]})
    viewer = TestClient(app)
    viewer.post("/api/auth/dev", json={"email": "ro2@example.com", "role": "ReadOnly2"})
    v_row = next(
        u for u in viewer.get("/api/users").json()["items"] if u["email"] == "loc2@example.com"
    )
    assert v_row["location"] is None
    assert v_row["timezone"] is None


def test_kick_response_hides_location_from_non_manager(client):
    """presence.kick alone must not leak the target's GPS in the kick response."""
    owner = TestClient(app)
    ou = owner.post("/api/auth/dev", json={"email": "gps@example.com", "role": "user"}).json()
    owner.post("/api/auth/location", json={"latitude": 51.5, "longitude": -0.12, "accuracy": 8})

    # A role with presence.kick but NOT users.manage (mirrors the seeded "Manager").
    _login(client, "admin@example.com", role="admin")
    client.post(
        "/api/roles",
        json={"name": "Moderator", "permission_keys": ["presence.view", "presence.kick"]},
    )
    mod = TestClient(app)
    mod.post("/api/auth/dev", json={"email": "mod@example.com", "role": "Moderator"})

    resp = mod.post(f"/api/users/{ou['id']}/kick")
    assert resp.status_code == 200
    body = resp.json()
    assert body["last_latitude"] is None
    assert body["last_longitude"] is None
    assert body["last_location_accuracy"] is None

    # ...but an admin (users.manage) kicking still sees coordinates.
    admin_resp = client.post(f"/api/users/{ou['id']}/kick")
    assert admin_resp.json()["last_latitude"] == 51.5


def test_online_false_filters_to_offline_users(client):
    """FND-029: online=false returns only offline users instead of acting as a no-op."""
    online_u = (
        TestClient(app)
        .post("/api/auth/dev", json={"email": "on@example.com", "role": "user"})
        .json()
    )
    _login(client, "admin@example.com", role="admin")

    on_ids = {u["id"] for u in client.get("/api/users", params={"online": "true"}).json()["items"]}
    assert online_u["id"] in on_ids

    off_ids = {
        u["id"] for u in client.get("/api/users", params={"online": "false"}).json()["items"]
    }
    assert online_u["id"] not in off_ids  # a freshly-active user must not appear in "offline"


def test_bulk_action_reports_db_error_without_aborting(client, monkeypatch):
    """FND-017: a commit-time DB error is reported per-item, not raised as a 500."""
    from sqlalchemy.exc import OperationalError

    from app.routers import users as users_router

    _login(client, "admin@example.com", role="admin")
    u = (
        TestClient(app)
        .post("/api/auth/dev", json={"email": "dberr@example.com", "role": "user"})
        .json()
    )

    def boom(*a, **k):
        raise OperationalError("stmt", {}, Exception("simulated deadlock"))

    monkeypatch.setattr(users_router, "revoke_all_for_user", boom)
    resp = client.post("/api/users/bulk", json={"ids": [u["id"]], "action": "deactivate"})
    assert resp.status_code == 200
    assert resp.json()["applied"] == 0
    assert resp.json()["results"][0]["error"] == "DB_ERROR"


def test_user_list_pagination(client):
    _login(client, "admin@example.com", role="admin")
    for i in range(4):
        TestClient(app).post("/api/auth/dev", json={"email": f"pg{i}@example.com", "role": "user"})

    full = client.get("/api/users").json()
    assert full["total"] >= 5  # admin + 4

    page = client.get("/api/users", params={"limit": 2, "offset": 0}).json()
    assert page["total"] == full["total"]  # total counts all matches, not just the page
    assert len(page["items"]) == 2

    page2 = client.get("/api/users", params={"limit": 2, "offset": 2}).json()
    assert len(page2["items"]) == 2
    assert {u["id"] for u in page["items"]}.isdisjoint({u["id"] for u in page2["items"]})
