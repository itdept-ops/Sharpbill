"""Contacts CRM: CRUD, permissions, ownership, stats."""

from fastapi.testclient import TestClient

from app.main import app


def _login(client, email, role="user"):
    assert client.post("/api/auth/dev", json={"email": email, "role": role}).status_code == 200


def test_user_can_crud_contacts(client):
    _login(client, "rep@example.com", role="user")  # 'user' role has contacts.read/write
    created = client.post(
        "/api/contacts",
        json={
            "first_name": "Ada",
            "last_name": "Lovelace",
            "company": "Analytical",
            "status": "lead",
        },
    )
    assert created.status_code == 201
    c = created.json()
    assert c["full_name"] == "Ada Lovelace"
    assert c["owner_name"] == "rep"  # defaults to the creator

    cid = c["id"]
    assert client.get(f"/api/contacts/{cid}").status_code == 200
    upd = client.patch(f"/api/contacts/{cid}", json={"status": "customer", "title": "Countess"})
    assert upd.status_code == 200
    assert upd.json()["status"] == "customer"

    assert client.get("/api/contacts").json()["total"] == 1
    assert client.delete(f"/api/contacts/{cid}").status_code == 204
    assert client.get("/api/contacts").json()["total"] == 0


def test_contacts_filters(client):
    _login(client, "rep@example.com", role="user")
    client.post(
        "/api/contacts", json={"first_name": "Grace", "company": "Navy", "status": "active"}
    )
    client.post(
        "/api/contacts", json={"first_name": "Alan", "company": "Bletchley", "status": "lead"}
    )

    assert client.get("/api/contacts", params={"search": "grace"}).json()["total"] == 1
    assert client.get("/api/contacts", params={"status": "lead"}).json()["total"] == 1
    assert client.get("/api/contacts", params={"mine": "true"}).json()["total"] == 2


def test_contacts_require_permission(client):
    # A custom role with no contacts permissions.
    _login(client, "admin@example.com", role="admin")
    client.post("/api/roles", json={"name": "NoContacts", "permission_keys": ["presence.view"]})
    blind = TestClient(app)
    bu = blind.post("/api/auth/dev", json={"email": "blind@example.com", "role": "user"}).json()
    client.patch(
        f"/api/users/{bu['id']}/role",
        json={
            "role_id": next(
                r["id"] for r in client.get("/api/roles").json() if r["name"] == "NoContacts"
            )
        },
    )
    assert blind.get("/api/contacts").status_code == 403
    assert blind.post("/api/contacts", json={"first_name": "X"}).status_code == 403


def test_contact_stats(client):
    _login(client, "rep@example.com", role="user")
    for s in ("lead", "lead", "customer"):
        client.post("/api/contacts", json={"first_name": "C", "status": s})
    stats = client.get("/api/contacts/stats").json()
    assert stats["total"] == 3
    by = {r["status"]: r["count"] for r in stats["by_status"]}
    assert by["lead"] == 2 and by["customer"] == 1
    assert len(stats["created"]) == 14


def test_unknown_owner_rejected(client):
    _login(client, "rep@example.com", role="user")
    resp = client.post("/api/contacts", json={"first_name": "Z", "owner_id": 999999})
    assert resp.status_code == 400
    assert resp.json()["detail"]["code"] == "UNKNOWN_OWNER"
