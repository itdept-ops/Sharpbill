"""Single-use OIDC login nonce store + endpoint."""

from datetime import UTC, datetime, timedelta

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
    n = client.get("/api/auth/nonce").json()["nonce"]
    assert isinstance(n, str) and len(n) > 20
    assert consume_nonce(n) is True  # the issued nonce is valid exactly once
    assert consume_nonce(n) is False
