"""Single-use OIDC login nonces, stored in the DB (shared across workers/instances/restarts).

Flow: the SPA fetches a nonce (GET /api/auth/nonce) before starting a provider sign-in and passes
it into the OIDC request; the provider echoes it in the id_token's `nonce` claim, which the verifier
consumes here exactly once. Consuming a single-use nonce binds the token to *this* app's login
request AND defeats id_token replay across processes (the in-memory replay guard only covers one
worker, since it is per-process).
"""

import secrets
from datetime import UTC, datetime, timedelta

from sqlalchemy import delete

from app.db import SessionLocal
from app.models import LoginNonce

# How long a user has to complete a sign-in after the nonce is issued.
_NONCE_TTL_SECONDS = 600


def _now() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def issue_nonce() -> str:
    """Mint a random nonce, persist it with a short TTL, and return it."""
    nonce = secrets.token_urlsafe(32)
    with SessionLocal() as db:
        db.add(LoginNonce(nonce=nonce, expires_at=_now() + timedelta(seconds=_NONCE_TTL_SECONDS)))
        db.commit()
    return nonce


def consume_nonce(nonce: str) -> bool:
    """Atomically consume a nonce. Return True iff it existed and was unexpired (single-use).

    The consume is a single conditional DELETE, so two concurrent presentations of the same nonce
    race safely: exactly one DELETE affects the row and returns True; the other returns False.
    """
    if not nonce:
        return False
    now = _now()
    with SessionLocal() as db:
        db.execute(delete(LoginNonce).where(LoginNonce.expires_at <= now))  # opportunistic prune
        result = db.execute(
            delete(LoginNonce).where(LoginNonce.nonce == nonce, LoginNonce.expires_at > now)
        )
        db.commit()
        return (result.rowcount or 0) == 1
