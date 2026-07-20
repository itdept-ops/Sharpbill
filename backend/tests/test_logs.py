"""Request activity log tests."""

from app.models import RequestLog


def test_logs_require_permission(client):
    client.post("/api/auth/dev", json={"email": "plain@example.com", "role": "user"})
    assert client.get("/api/admin/logs").status_code == 403


def test_requests_are_logged_with_user_and_endpoint(client):
    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    # A meaningful request that should be recorded.
    resp = client.post("/api/roles", json={"name": "Logged", "permission_keys": []})
    assert resp.status_code == 201

    body = client.get("/api/admin/logs").json()
    assert body["total"] >= 1
    entry = next(
        (e for e in body["items"] if e["path"] == "/api/roles" and e["method"] == "POST"), None
    )
    assert entry is not None
    assert entry["status_code"] == 201
    assert entry["user_email"] == "admin@example.com"
    assert entry["ip"]  # captured


def test_logs_method_filter(client):
    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    resp = client.post("/api/roles", json={"name": "Zeta", "permission_keys": []})
    assert resp.status_code == 201
    client.get("/api/users")  # a logged GET

    body = client.get("/api/admin/logs", params={"method": "POST"}).json()
    assert body["total"] >= 1
    assert all(e["method"] == "POST" for e in body["items"])
    assert any(e["path"] == "/api/roles" for e in body["items"])


def test_noisy_paths_are_not_logged(client):
    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    client.get("/api/auth/me")  # skipped path
    client.get("/api/health")  # skipped path
    body = client.get("/api/admin/logs").json()
    assert not any(e["path"] in ("/api/auth/me", "/api/health") for e in body["items"])


def test_logs_support_stable_bounded_cursor_pages(client, db):
    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    db.add_all(
        [
            RequestLog(
                method="TRACE",
                path=f"/api/cursor/{index}",
                user_id=None,
                ip="127.0.0.1",
                status_code=200,
            )
            for index in range(5)
        ]
    )
    db.commit()

    first = client.get("/api/admin/logs", params={"method": "TRACE", "limit": 2})
    assert first.status_code == 200
    first_body = first.json()
    assert first_body["total"] == 5
    assert len(first_body["items"]) == 2
    cursor = first_body["next_cursor"]
    assert cursor == first_body["items"][-1]["id"]

    second = client.get(
        "/api/admin/logs",
        params={"method": "TRACE", "limit": 2, "before_id": cursor},
    )
    assert second.status_code == 200
    second_body = second.json()
    assert second_body["total"] == 5
    assert len(second_body["items"]) == 2
    assert max(item["id"] for item in second_body["items"]) < cursor
    assert {item["id"] for item in first_body["items"]}.isdisjoint(
        item["id"] for item in second_body["items"]
    )


def test_logs_reject_pathological_deep_offset(client):
    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    response = client.get("/api/admin/logs", params={"offset": 10_001})
    assert response.status_code == 422
