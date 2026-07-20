"""Single-use OIDC login nonces, stored in the DB (shared across workers/instances/restarts).

Flow: the SPA requests a nonce (POST /api/auth/nonce) before starting a provider sign-in and passes
it into the OIDC request; the provider echoes it in the id_token's `nonce` claim, which the verifier
consumes here exactly once. Consuming a single-use nonce binds the token to *this* app's login
request AND defeats id_token replay across processes (the in-memory replay guard only covers one
worker, since it is per-process).
"""

import json
import logging
import secrets
import threading
from datetime import UTC, datetime, timedelta

from sqlalchemy import delete, func, select
from sqlalchemy.orm import Session

from app.config import settings
from app.db import SessionLocal
from app.models import LoginNonce

# How long a user has to complete a sign-in after the nonce is issued.
_NONCE_TTL_SECONDS = 600
_MAX_OUTSTANDING_NONCES = 5_000
_issue_lock = threading.Lock()
_security_log = logging.getLogger("app.security")


class NonceCapacityError(RuntimeError):
    """Raised when the bounded pending-login store is full."""


def _now() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def _record_lifecycle(event: str, outcome: str, **details: int | str) -> None:
    """Emit countable nonce telemetry without ever recording the opaque nonce value."""
    _security_log.info(
        "%s",
        json.dumps(
            {"event": event, "outcome": outcome, **details},
            separators=(",", ":"),
            sort_keys=True,
        ),
    )


def prune_expired_nonces(
    db: Session, *, now: datetime | None = None, limit: int | None = None
) -> int:
    """Delete a bounded expired-nonce batch, independently of evidence legal holds."""
    current = now or _now()
    batch_size = limit or settings.nonce_prune_batch_size
    expired = list(
        db.scalars(
            select(LoginNonce.nonce)
            .where(LoginNonce.expires_at <= current)
            .order_by(LoginNonce.expires_at, LoginNonce.nonce)
            .limit(batch_size)
        )
    )
    if expired:
        result = db.execute(delete(LoginNonce).where(LoginNonce.nonce.in_(expired)))
        return result.rowcount or 0
    return 0


def issue_nonce() -> str:
    """Mint a nonce while bounding expired cleanup and total outstanding login state."""
    # The production image intentionally runs one worker while this guard is process-local.
    # Shared rate/state infrastructure must replace it before horizontal scaling.
    with _issue_lock, SessionLocal() as db:
        now = _now()
        pruned = prune_expired_nonces(db, now=now)
        outstanding = (
            db.scalar(
                select(func.count()).select_from(LoginNonce).where(LoginNonce.expires_at > now)
            )
            or 0
        )
        if outstanding >= _MAX_OUTSTANDING_NONCES:
            db.commit()  # retain the bounded expired-row cleanup even while refusing issuance
            _record_lifecycle(
                "oidc_nonce_issue",
                "rejected_capacity",
                outstanding=outstanding,
                pruned=pruned,
            )
            raise NonceCapacityError("pending login nonce capacity reached")
        nonce = secrets.token_urlsafe(32)
        db.add(LoginNonce(nonce=nonce, expires_at=now + timedelta(seconds=_NONCE_TTL_SECONDS)))
        db.commit()
        _record_lifecycle(
            "oidc_nonce_issue", "succeeded", outstanding=outstanding + 1, pruned=pruned
        )
        return nonce


def consume_nonce(nonce: str) -> bool:
    """Atomically consume a nonce. Return True iff it existed and was unexpired (single-use).

    The consume is a single conditional DELETE, so two concurrent presentations of the same nonce
    race safely: exactly one DELETE affects the row and returns True; the other returns False.
    """
    if not nonce:
        _record_lifecycle("oidc_nonce_consume", "rejected_missing")
        return False
    now = _now()
    with SessionLocal() as db:
        pruned = prune_expired_nonces(db, now=now)
        result = db.execute(
            delete(LoginNonce).where(LoginNonce.nonce == nonce, LoginNonce.expires_at > now)
        )
        db.commit()
        consumed = (result.rowcount or 0) == 1
        _record_lifecycle(
            "oidc_nonce_consume",
            "succeeded" if consumed else "rejected_invalid_or_replayed",
            pruned=pruned,
        )
        return consumed
