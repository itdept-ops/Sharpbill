"""Negative tests for session-JWT validation: alg pinning, required claims, expiry, tampering.

These guard the crypto that underpins every authenticated request (FND-026) — a silent
regression here (e.g. dropping the algorithm pin) would otherwise pass a green suite.
"""

from datetime import UTC, datetime, timedelta

import jwt
import pytest

from app.auth.jwt import create_session_token, decode_session_token
from app.config import settings


def _claims(**over):
    now = datetime.now(UTC)
    base = {
        "sub": "1",
        "jti": "j1",
        "iss": settings.session_jwt_issuer,
        "aud": settings.session_jwt_audience,
        "token_type": "session",
        "iat": now,
        "exp": now + timedelta(hours=1),
    }
    base.update(over)
    return base


def _encode(claims=None, *, secret=None, kid=None, algorithm="HS256"):
    return jwt.encode(
        claims or _claims(),
        secret or settings.session_jwt_secret,
        algorithm=algorithm,
        headers={"kid": kid or settings.session_jwt_active_kid, "typ": "JWT"},
    )


def test_roundtrip_decodes_sub_and_jti():
    payload = decode_session_token(create_session_token(42, "jti-xyz"))
    assert payload["sub"] == "42" and payload["jti"] == "jti-xyz"


def test_rejects_wrong_secret():
    bad = _encode(secret="a-different-secret-of-adequate-length-000")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(bad)


def test_rejects_alg_none():
    tok = jwt.encode(
        _claims(),
        key="",
        algorithm="none",
        headers={"kid": settings.session_jwt_active_kid, "typ": "JWT"},
    )
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tok)


def test_rejects_non_hs256_algorithm():
    # A different HMAC algorithm must be refused before key selection (algorithm is pinned).
    hs512_key = "hs512-test-key-" + "0123456789abcdef" * 4
    tok = _encode(secret=hs512_key, algorithm="HS512")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tok)


@pytest.mark.parametrize("missing", ["sub", "jti", "iat", "exp", "iss", "aud", "token_type"])
def test_rejects_missing_required_claim(missing):
    claims = _claims()
    del claims[missing]
    tok = _encode(claims)
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tok)


def test_rejects_expired_token():
    now = datetime.now(UTC)
    tok = _encode(_claims(iat=now - timedelta(hours=2), exp=now - timedelta(hours=1)))
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tok)


def test_rejects_tampered_signature():
    tok = create_session_token(1, "j")
    tampered = tok[:-4] + ("aaaa" if tok[-4:] != "aaaa" else "bbbb")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tampered)


@pytest.mark.parametrize(
    "claim,value",
    [
        ("iss", "another-issuer"),
        ("aud", "another-audience"),
        ("token_type", "password-reset"),
    ],
)
def test_rejects_wrong_session_contract(claim, value):
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(_encode(_claims(**{claim: value})))


def test_rejects_missing_or_unknown_kid():
    missing = jwt.encode(
        _claims(), settings.session_jwt_secret, algorithm="HS256", headers={"typ": "JWT"}
    )
    unknown = _encode(kid="unknown-key-id")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(missing)
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(unknown)


def test_accepts_previous_key_during_rotation_then_rejects_after_removal(monkeypatch):
    previous = "previous-signing-secret-0123456789abcdef012345"
    token = _encode(secret=previous, kid=settings.jwt_key_id(previous))

    monkeypatch.setattr(settings, "session_jwt_previous_secrets", previous)
    assert decode_session_token(token)["sub"] == "1"

    monkeypatch.setattr(settings, "session_jwt_previous_secrets", "")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(token)
