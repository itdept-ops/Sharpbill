"""Single-use OIDC login nonce store + endpoint."""

import json
from datetime import UTC, datetime, timedelta

from sqlalchemy import func, select

from app.auth.nonce import consume_nonce, issue_nonce
from app.models import LoginNonce


def test_nonce_is_single_use():
    n = issue_nonce()
    assert consume_nonce(n) is True  # first presentation consumes it
    assert consume_nonce(n) is False  # a replay of the same nonce is rejected


def test_consume_unknown_or_empty_nonce_is_false():
    assert consume_nonce("never-issued") is False
    assert consume_nonce("") is False


def test_expired_nonce_is_rejected(db):
    past = datetime.now(UTC).replace(tzinfo=None) - timedelta(seconds=1)
    db.add(LoginNonce(nonce="stale-nonce", expires_at=past))
    db.commit()
    assert consume_nonce("stale-nonce") is False


def test_nonce_endpoint_issues_a_consumable_nonce(client):
    response = client.post("/api/auth/nonce")
    assert response.status_code == 201
    assert response.headers["Cache-Control"].startswith("no-store")
    n = response.json()["nonce"]
    assert isinstance(n, str) and len(n) > 20
    assert consume_nonce(n) is True  # the issued nonce is valid exactly once
    assert consume_nonce(n) is False


def test_nonce_get_is_rejected_without_creating_state(client, db):
    before = db.scalar(select(func.count()).select_from(LoginNonce))
    response = client.get("/api/auth/nonce")
    assert response.status_code == 405
    assert response.json()["detail"]["code"] == "METHOD_NOT_ALLOWED"
    db.expire_all()
    assert db.scalar(select(func.count()).select_from(LoginNonce)) == before


def test_nonce_issuance_prunes_expired_rows_in_a_bounded_pass(client, db):
    past = datetime.now(UTC).replace(tzinfo=None) - timedelta(minutes=1)
    db.add_all(LoginNonce(nonce=f"expired-{i}", expires_at=past) for i in range(3))
    db.commit()

    assert client.post("/api/auth/nonce").status_code == 201
    db.expire_all()
    expired_count = db.scalar(
        select(func.count()).select_from(LoginNonce).where(LoginNonce.expires_at <= past)
    )
    assert expired_count == 0


def test_nonce_capacity_is_fail_closed(client, db, monkeypatch):
    from app.auth import nonce as nonce_store

    monkeypatch.setattr(nonce_store, "_MAX_OUTSTANDING_NONCES", 1)
    future = datetime.now(UTC).replace(tzinfo=None) + timedelta(minutes=5)
    db.add(LoginNonce(nonce="at-capacity", expires_at=future))
    db.commit()

    response = client.post("/api/auth/nonce")
    assert response.status_code == 503
    assert response.json()["detail"]["code"] == "LOGIN_STATE_CAPACITY"
    assert response.headers["Retry-After"] == "30"


def test_nonce_lifecycle_telemetry_is_countable_and_never_records_nonce(monkeypatch):
    from app.auth import nonce as nonce_store

    messages: list[str] = []

    def capture(format_string: str, payload: str) -> None:
        assert format_string == "%s"
        messages.append(payload)

    monkeypatch.setattr(nonce_store._security_log, "info", capture)
    nonce = issue_nonce()
    assert consume_nonce(nonce) is True
    assert consume_nonce(nonce) is False

    events = [json.loads(message) for message in messages]
    assert [(event["event"], event["outcome"]) for event in events] == [
        ("oidc_nonce_issue", "succeeded"),
        ("oidc_nonce_consume", "succeeded"),
        ("oidc_nonce_consume", "rejected_invalid_or_replayed"),
    ]
    assert all(nonce not in message for message in messages)
