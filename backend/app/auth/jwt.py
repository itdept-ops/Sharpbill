from datetime import UTC, datetime, timedelta

import jwt
from fastapi import Response

from app.config import settings

COOKIE_NAME = "session"


def create_session_token(user_id: int) -> str:
    now = datetime.now(UTC)
    return jwt.encode(
        {
            "sub": str(user_id),
            "iat": now,
            "exp": now + timedelta(seconds=settings.session_ttl_seconds),
        },
        settings.session_jwt_secret,
        algorithm="HS256",
    )


def decode_session_token(token: str) -> dict:
    """Return the decoded payload (sub, iat, exp). Raises jwt.InvalidTokenError on failure."""
    return jwt.decode(
        token,
        settings.session_jwt_secret,
        algorithms=["HS256"],
        options={"require": ["exp", "iat", "sub"]},
    )


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
