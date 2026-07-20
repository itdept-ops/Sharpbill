from datetime import UTC, datetime, timedelta

import jwt
from fastapi import Response

from app.config import settings

COOKIE_NAME = settings.session_cookie_name
_TOKEN_TYPE = "session"


def create_session_token(user_id: int, jti: str) -> str:
    now = datetime.now(UTC)
    return jwt.encode(
        {
            "sub": str(user_id),
            "jti": jti,  # binds the cookie to a specific server-side session row (per device)
            "iss": settings.session_jwt_issuer,
            "aud": settings.session_jwt_audience,
            "token_type": _TOKEN_TYPE,
            "iat": now,
            "exp": now + timedelta(seconds=settings.session_ttl_seconds),
        },
        settings.session_jwt_secret,
        algorithm="HS256",
        headers={"kid": settings.session_jwt_active_kid, "typ": "JWT"},
    )


def decode_session_token(token: str) -> dict:
    """Verify the session contract against the active/overlap keyring and return its claims."""
    header = jwt.get_unverified_header(token)
    if header.get("alg") != "HS256" or header.get("typ") != "JWT":
        raise jwt.InvalidTokenError("unexpected session token header")
    kid = header.get("kid")
    if not isinstance(kid, str):
        raise jwt.InvalidTokenError("session token is missing a key id")
    secret = settings.session_jwt_keyring.get(kid)
    if secret is None:
        raise jwt.InvalidTokenError("session token key id is not trusted")

    payload = jwt.decode(
        token,
        secret,
        algorithms=["HS256"],
        audience=settings.session_jwt_audience,
        issuer=settings.session_jwt_issuer,
        options={
            "require": ["exp", "iat", "sub", "jti", "iss", "aud", "token_type"],
        },
    )
    if payload.get("token_type") != _TOKEN_TYPE:
        raise jwt.InvalidTokenError("unexpected token type")
    if not isinstance(payload.get("sub"), str) or not payload["sub"].isdigit():
        raise jwt.InvalidTokenError("invalid session subject")
    if not isinstance(payload.get("jti"), str) or not payload["jti"]:
        raise jwt.InvalidTokenError("invalid session id")
    return payload


def set_session_cookie(response: Response, token: str) -> None:
    response.set_cookie(
        COOKIE_NAME,
        token,
        max_age=settings.session_ttl_seconds,
        httponly=True,
        secure=settings.cookie_secure,
        samesite="lax",
        path="/",
    )


def clear_session_cookie(response: Response) -> None:
    response.delete_cookie(
        COOKIE_NAME,
        path="/",
        httponly=True,
        secure=settings.cookie_secure,
        samesite="lax",
    )
