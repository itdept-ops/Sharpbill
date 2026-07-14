"""Request activity log tests."""


def test_logs_require_permission(client):
    client.post("/api/auth/dev", json={"email": "plain@example.com", "role": "user"})
    assert client.get("/api/admin/logs").status_code == 403


def test_requests_are_logged_with_user_and_endpoint(client):
    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    # A meaningful request that should be recorded.
    assert client.post("/api/contacts", json={"first_name": "Log Me"}).status_code == 201

    body = client.get("/api/admin/logs").json()
    assert body["total"] >= 1
    entry = next(
        (e for e in body["items"] if e["path"] == "/api/contacts" and e["method"] == "POST"), None
    )
    assert entry is not None
    assert entry["status_code"] == 201
    assert entry["user_email"] == "admin@example.com"
    assert entry["ip"]  # captured


def test_noisy_paths_are_not_logged(client):
    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    client.get("/api/auth/me")  # skipped path
    client.get("/api/health")  # skipped path
    body = client.get("/api/admin/logs").json()
    assert not any(e["path"] in ("/api/auth/me", "/api/health") for e in body["items"])
