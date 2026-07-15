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
    base = {"sub": "1", "jti": "j1", "iat": now, "exp": now + timedelta(hours=1)}
    base.update(over)
    return base


def test_roundtrip_decodes_sub_and_jti():
    payload = decode_session_token(create_session_token(42, "jti-xyz"))
    assert payload["sub"] == "42" and payload["jti"] == "jti-xyz"


def test_rejects_wrong_secret():
    bad = jwt.encode(_claims(), "a-different-secret-of-adequate-length-000", algorithm="HS256")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(bad)


def test_rejects_alg_none():
    tok = jwt.encode(_claims(), key="", algorithm="none")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tok)


def test_rejects_non_hs256_algorithm():
    # Signed with the right secret but a different HMAC alg — must be refused (alg is pinned).
    tok = jwt.encode(_claims(), settings.session_jwt_secret, algorithm="HS512")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tok)


@pytest.mark.parametrize("missing", ["sub", "jti", "iat", "exp"])
def test_rejects_missing_required_claim(missing):
    claims = _claims()
    del claims[missing]
    tok = jwt.encode(claims, settings.session_jwt_secret, algorithm="HS256")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tok)


def test_rejects_expired_token():
    now = datetime.now(UTC)
    tok = jwt.encode(
        _claims(iat=now - timedelta(hours=2), exp=now - timedelta(hours=1)),
        settings.session_jwt_secret,
        algorithm="HS256",
    )
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tok)


def test_rejects_tampered_signature():
    tok = create_session_token(1, "j")
    tampered = tok[:-4] + ("aaaa" if tok[-4:] != "aaaa" else "bbbb")
    with pytest.raises(jwt.InvalidTokenError):
        decode_session_token(tampered)
