"""Single-use provider-token replay guard."""

import time

import pytest

from app.auth import ProviderTokenError
from app.auth import google as google_auth
from app.auth.replay import _reset_for_tests, check_replay


def test_replay_blocks_exact_second_use():
    _reset_for_tests()
    assert check_replay("token-abc", 60) is False  # first presentation is fine
    assert check_replay("token-abc", 60) is True  # identical token → replay


def test_replay_allows_distinct_tokens():
    _reset_for_tests()
    assert check_replay("token-A", 60) is False
    assert check_replay("token-B", 60) is False  # a fresh re-login mints a different token


def test_replay_does_not_record_expired_ttl():
    _reset_for_tests()
    assert check_replay("token-exp", 0) is False  # already expired → not recorded
    assert check_replay("token-exp", 60) is False  # so a later valid presentation still passes


def test_google_verifier_rejects_replayed_token(monkeypatch):
    """A captured Google id_token cannot be presented twice within its validity window."""
    _reset_for_tests()
    monkeypatch.setattr(google_auth.settings, "google_client_id", "test-cid")
    claims = {
        "iss": "https://accounts.google.com",
        "email_verified": True,
        "sub": "sub-123",
        "email": "Person@Example.com",
        "name": "Person",
        "exp": time.time() + 3600,
    }
    monkeypatch.setattr(google_auth.google_id_token, "verify_oauth2_token", lambda *a, **k: claims)

    raw = "google-raw-token-xyz"
    ident = google_auth.verify_google_id_token(raw)
    assert ident.subject == "sub-123"
    assert ident.email == "person@example.com"  # normalized

    with pytest.raises(ProviderTokenError):
        google_auth.verify_google_id_token(raw)  # same token replayed → rejected
