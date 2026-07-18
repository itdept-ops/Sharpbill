"""Single-use provider-token replay guard."""

import time

import pytest

from app.auth import ProviderTokenError
from app.auth import google as google_auth
from app.auth.nonce import issue_nonce
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


def _google_claims(nonce, **over):
    claims = {
        "iss": "https://accounts.google.com",
        "email_verified": True,
        "sub": "sub-123",
        "email": "Person@Example.com",
        "name": "Person",
        "nonce": nonce,
        "exp": time.time() + 3600,
    }
    claims.update(over)
    return claims


def test_google_verifier_rejects_replayed_token(monkeypatch):
    """A captured Google id_token cannot be presented twice: its single-use nonce is consumed."""
    _reset_for_tests()
    monkeypatch.setattr(google_auth.settings, "google_client_id", "test-cid")
    claims = _google_claims(issue_nonce())
    monkeypatch.setattr(google_auth.google_id_token, "verify_oauth2_token", lambda *a, **k: claims)

    raw = "google-raw-token-xyz"
    ident = google_auth.verify_google_id_token(raw)
    assert ident.subject == "sub-123"
    assert ident.email == "person@example.com"  # normalized

    with pytest.raises(ProviderTokenError):
        google_auth.verify_google_id_token(raw)  # same token replayed → nonce already consumed


def test_google_verifier_requires_a_valid_nonce(monkeypatch):
    """A token whose nonce this app never issued (or that is missing) is rejected."""
    _reset_for_tests()
    monkeypatch.setattr(google_auth.settings, "google_client_id", "test-cid")

    no_nonce = _google_claims(nonce="")
    monkeypatch.setattr(
        google_auth.google_id_token, "verify_oauth2_token", lambda *a, **k: no_nonce
    )
    with pytest.raises(ProviderTokenError):
        google_auth.verify_google_id_token("tok-without-nonce")

    forged = _google_claims(nonce="a-nonce-we-never-issued")
    monkeypatch.setattr(google_auth.google_id_token, "verify_oauth2_token", lambda *a, **k: forged)
    with pytest.raises(ProviderTokenError):
        google_auth.verify_google_id_token("tok-forged-nonce")
