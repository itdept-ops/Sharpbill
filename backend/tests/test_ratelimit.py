"""Per-IP rate limiting on the sign-in surface."""


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
