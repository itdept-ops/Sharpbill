"""Per-IP rate limiting on the sign-in surface."""

from app import ratelimit


def test_dev_login_is_rate_limited(client):
    # The login bucket is 20/min/IP; from one IP the 21st+ attempt should be throttled.
    codes = [
        client.post("/api/auth/dev", json={"email": f"rl{i}@example.com"}).status_code
        for i in range(25)
    ]
    assert codes[:20] == [200] * 20  # first 20 pass
    assert 429 in codes[20:]  # the rest are throttled

    resp = client.post("/api/auth/dev", json={"email": "rl-final@example.com"})
    assert resp.status_code == 429
    assert resp.json()["detail"]["code"] == "RATE_LIMITED"
    assert resp.headers.get("Retry-After")  # tells the client when to retry


def test_rate_limit_window_is_isolated_per_test(client):
    # The autouse fixture resets the limiter, so a fresh test starts unthrottled.
    assert client.post("/api/auth/dev", json={"email": "fresh@example.com"}).status_code == 200


def test_nonce_has_a_tighter_independent_rate_limit(client):
    codes = [client.post("/api/auth/nonce").status_code for _ in range(31)]
    assert codes[:30] == [201] * 30
    assert codes[30] == 429


def test_health_probes_remain_available_after_api_bucket_is_exhausted(client, monkeypatch):
    from app import main

    monkeypatch.setattr(main, "_API_LIMIT", (1, 60))
    assert client.get("/api/auth/config").status_code == 200
    assert client.get("/api/auth/config").status_code == 429

    live = client.get("/api/health/live")
    ready = client.get("/api/health/ready")
    assert live.status_code == 200
    assert live.json() == {"status": "alive"}
    assert ready.status_code == 200
    assert ready.json()["status"] == "ready"


def test_readiness_has_an_independent_bounded_probe_bucket(client, monkeypatch):
    from app import main

    monkeypatch.setattr(main, "_READINESS_LIMIT", (1, 60))
    assert client.get("/api/health/ready").status_code == 200
    limited = client.get("/api/health/ready")
    assert limited.status_code == 429
    assert limited.json()["detail"]["code"] == "RATE_LIMITED"
    assert client.get("/api/health/live").status_code == 200


def test_boundary_rate_rejection_emits_correlated_structured_evidence(client, monkeypatch):
    from app import main

    evidence = []

    def capture(scope, *, status_code, code):
        evidence.append(
            {
                "status_code": status_code,
                "code": code,
                "method": scope["method"],
                "path": scope["path"],
                "client_ip": scope["client"][0],
                "request_id": scope["state"]["request_id"],
            }
        )

    monkeypatch.setattr(main, "_log_boundary_rejection", capture)
    monkeypatch.setattr(main, "_API_LIMIT", (0, 60))
    response = client.get("/api/auth/config", headers={"X-Request-ID": "rate-audit-1"})
    assert response.status_code == 429
    assert evidence == [
        {
            "status_code": 429,
            "code": "RATE_LIMITED",
            "method": "GET",
            "path": "/api/auth/config",
            "client_ip": "127.0.0.1",
            "request_id": "rate-audit-1",
        }
    ]


def test_trusted_proxy_resolution_runs_before_rate_limiting(client, monkeypatch):
    from app import main

    seen: list[str] = []

    def capture(key: str, *_args) -> int:
        seen.append(key)
        return 0

    monkeypatch.setattr(main, "rate_check", capture)
    assert client.get("/api/health", headers={"X-Forwarded-For": "203.0.113.55"}).status_code == 200
    assert seen[0] == "readiness:203.0.113.55"


def test_limiter_cardinality_has_a_hard_memory_bound(monkeypatch):
    ratelimit.reset()
    monkeypatch.setattr(ratelimit, "_MAX_WINDOWS", 3)
    assert ratelimit.check("ip:1", 10, 60) == 0
    assert ratelimit.check("ip:2", 10, 60) == 0
    assert ratelimit.check("ip:3", 10, 60) == 0
    assert ratelimit.check("ip:4", 10, 60) == 60
    assert len(ratelimit._windows) == 3
    assert "ip:4" not in ratelimit._windows
