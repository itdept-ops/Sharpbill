"""Durable security-event creation and the future SIEM dispatch boundary.

Business mutations call :func:`add_security_event` on their existing SQLAlchemy session before
commit. That makes the immutable event fact and its delivery cursor atomic with the protected
change. Authentication failures use :func:`commit_security_event` because there is intentionally
no successful business transaction to join.

No dispatcher is started here. A future external worker can claim immutable envelopes through
``claim_delivery_batch`` and acknowledge through the two state-transition functions without
receiving permission to rewrite event facts.
"""

import json
import re
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from hashlib import sha256
from typing import Any, Literal

from fastapi import Request
from sqlalchemy import and_, or_, select
from sqlalchemy.orm import Session

from app.config import settings
from app.models import SecurityEvent, SecurityEventDelivery

SecurityOutcome = Literal["success", "failure", "denied"]
SecuritySeverity = Literal["info", "warning", "critical"]

_EVENT_TYPE_RE = re.compile(r"^[a-z][a-z0-9_.-]{2,79}$")
_TARGET_TYPE_RE = re.compile(r"^[a-z][a-z0-9_.-]{0,39}$")
_FORBIDDEN_KEY_RE = re.compile(
    r"(?:authorization|cookie|credential|id[_-]?token|jwt|nonce|password|provider[_-]?subject|"
    r"secret|session[_-]?token|access[_-]?token|refresh[_-]?token)",
    re.IGNORECASE,
)
_MAX_METADATA_BYTES = 4096
_MAX_METADATA_DEPTH = 4
_MAX_LIST_ITEMS = 50
_SUMMARY_SAMPLE_ITEMS = 8


def _now() -> datetime:
    return datetime.now(UTC).replace(tzinfo=None)


def _worker_id(value: str) -> str:
    return value.strip()[:64]


def _clean_sequence(value: list | tuple, *, depth: int) -> list[Any]:
    if len(value) > _MAX_LIST_ITEMS:
        raise ValueError("security-event metadata list is too large")
    return [_clean_value(item, depth=depth + 1) for item in value]


def _clean_mapping(value: dict, *, depth: int) -> dict[str, Any]:
    cleaned: dict[str, Any] = {}
    for raw_key, raw_value in value.items():
        key = str(raw_key)
        if _FORBIDDEN_KEY_RE.search(key):
            raise ValueError(f"forbidden security-event metadata key: {key}")
        cleaned[key[:100]] = _clean_value(raw_value, depth=depth + 1)
    return cleaned


def _clean_value(value: Any, *, depth: int = 0) -> Any:
    if depth > _MAX_METADATA_DEPTH:
        raise ValueError("security-event metadata nesting is too deep")
    if value is None or isinstance(value, bool | int):
        return value
    if isinstance(value, float):
        if value != value or value in (float("inf"), float("-inf")):
            raise ValueError("security-event metadata must contain finite numbers")
        return value
    if isinstance(value, str):
        return value[:500]
    if isinstance(value, list | tuple):
        return _clean_sequence(value, depth=depth)
    if isinstance(value, dict):
        return _clean_mapping(value, depth=depth)
    raise ValueError(f"unsupported security-event metadata type: {type(value).__name__}")


def sanitize_metadata(metadata: dict[str, Any] | None) -> dict[str, Any]:
    """Validate a small JSON object and reject keys likely to carry secrets/opaque subjects."""
    cleaned = _clean_value(metadata or {})
    assert isinstance(cleaned, dict)
    encoded = json.dumps(cleaned, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    if len(encoded) > _MAX_METADATA_BYTES:
        raise ValueError("security-event metadata exceeds 4096 encoded bytes")
    return cleaned


def summarize_string_set(values: list[str] | set[str] | frozenset[str]) -> dict[str, Any]:
    """Return bounded, comparison-friendly evidence for a potentially large string set.

    RBAC requests allow up to 100 permission keys and each key can be long. Recording the raw
    list can exceed the durable event metadata contract and must never make an otherwise valid
    authorization change fail. A count and digest preserve exact change detection; the capped
    sample remains useful to operators without allowing event size to grow with the catalog.
    """
    normalized = sorted({str(value)[:100] for value in values})
    canonical = "\n".join(normalized).encode("utf-8")
    return {
        "count": len(normalized),
        "sha256": sha256(canonical).hexdigest(),
        "sample": normalized[:_SUMMARY_SAMPLE_ITEMS],
        "sample_truncated": len(normalized) > _SUMMARY_SAMPLE_ITEMS,
    }


def _request_context(request: Request | None) -> tuple[str | None, str | None]:
    if request is None:
        return None, None
    request_id = getattr(request.state, "request_id", None)
    ip = request.client.host if request.client else None
    return (
        str(request_id)[:64] if request_id else None,
        str(ip)[:45] if ip else None,
    )


def add_security_event(
    db: Session,
    *,
    event_type: str,
    outcome: SecurityOutcome,
    severity: SecuritySeverity = "info",
    request: Request | None = None,
    actor_user_id: int | None = None,
    target_type: str | None = None,
    target_id: str | int | None = None,
    metadata: dict[str, Any] | None = None,
) -> SecurityEvent:
    """Stage one immutable event and pending-delivery row on the caller's transaction."""
    if not _EVENT_TYPE_RE.fullmatch(event_type):
        raise ValueError(f"invalid security event type: {event_type}")
    if target_type is not None and not _TARGET_TYPE_RE.fullmatch(target_type):
        raise ValueError(f"invalid security event target type: {target_type}")
    request_id, source_ip = _request_context(request)
    event = SecurityEvent(
        event_type=event_type,
        outcome=outcome,
        severity=severity,
        request_id=request_id,
        actor_user_id=actor_user_id,
        target_type=target_type,
        target_id=(str(target_id)[:128] if target_id is not None else None),
        source_ip=source_ip,
        event_metadata=sanitize_metadata(metadata),
        retention_until=_now() + timedelta(days=settings.security_event_retention_days),
    )
    db.add(event)
    db.flush()
    db.add(SecurityEventDelivery(event_id=event.id))
    return event


def commit_security_event(db: Session, **kwargs: Any) -> SecurityEvent:
    """Persist an event outside a successful business mutation (notably denied authentication)."""
    event = add_security_event(db, **kwargs)
    db.commit()
    return event


@dataclass(frozen=True)
class DeliveryEnvelope:
    event_id: int
    event_type: str
    outcome: str
    severity: str
    request_id: str | None
    actor_user_id: int | None
    target_type: str | None
    target_id: str | None
    source_ip: str | None
    metadata: dict[str, Any]
    occurred_at: datetime


def claim_delivery_batch(
    db: Session,
    *,
    worker_id: str,
    limit: int = 100,
    lease_seconds: int = 60,
    now: datetime | None = None,
) -> list[DeliveryEnvelope]:
    """Lease a bounded pending batch using ``SKIP LOCKED`` for concurrent dispatchers."""
    if not 1 <= limit <= 500:
        raise ValueError("delivery batch limit must be between 1 and 500")
    if not 5 <= lease_seconds <= 900:
        raise ValueError("delivery lease must be between 5 and 900 seconds")
    worker = _worker_id(worker_id)
    if not worker:
        raise ValueError("worker_id is required")
    current = now or _now()
    eligible = or_(
        and_(
            SecurityEventDelivery.status.in_(("pending", "retry")),
            SecurityEventDelivery.next_attempt_at <= current,
        ),
        and_(
            SecurityEventDelivery.status == "leased",
            SecurityEventDelivery.lease_expires_at <= current,
        ),
    )
    deliveries = list(
        db.scalars(
            select(SecurityEventDelivery)
            .where(eligible)
            .order_by(SecurityEventDelivery.event_id)
            .limit(limit)
            .with_for_update(skip_locked=True)
        )
    )
    lease_expiry = current + timedelta(seconds=lease_seconds)
    for delivery in deliveries:
        delivery.status = "leased"
        delivery.lease_owner = worker
        delivery.lease_expires_at = lease_expiry
    db.commit()
    if not deliveries:
        return []
    events = {
        event.id: event
        for event in db.scalars(
            select(SecurityEvent).where(
                SecurityEvent.id.in_([delivery.event_id for delivery in deliveries])
            )
        )
    }
    return [
        DeliveryEnvelope(
            event_id=event.id,
            event_type=event.event_type,
            outcome=event.outcome,
            severity=event.severity,
            request_id=event.request_id,
            actor_user_id=event.actor_user_id,
            target_type=event.target_type,
            target_id=event.target_id,
            source_ip=event.source_ip,
            metadata=event.event_metadata,
            occurred_at=event.occurred_at,
        )
        for delivery in deliveries
        if (event := events.get(delivery.event_id)) is not None
    ]


def mark_delivery_succeeded(
    db: Session, *, event_id: int, worker_id: str, now: datetime | None = None
) -> bool:
    delivery = db.get(SecurityEventDelivery, event_id, with_for_update=True)
    if (
        delivery is None
        or delivery.status != "leased"
        or delivery.lease_owner != _worker_id(worker_id)
    ):
        db.rollback()
        return False
    current = now or _now()
    delivery.status = "delivered"
    delivery.attempts += 1
    delivery.last_attempt_at = current
    delivery.delivered_at = current
    delivery.lease_owner = None
    delivery.lease_expires_at = None
    delivery.last_error = None
    db.commit()
    return True


def mark_delivery_failed(
    db: Session,
    *,
    event_id: int,
    worker_id: str,
    error: str,
    max_attempts: int = 10,
    now: datetime | None = None,
) -> bool:
    delivery = db.get(SecurityEventDelivery, event_id, with_for_update=True)
    if (
        delivery is None
        or delivery.status != "leased"
        or delivery.lease_owner != _worker_id(worker_id)
    ):
        db.rollback()
        return False
    current = now or _now()
    delivery.attempts += 1
    delivery.last_attempt_at = current
    delivery.status = "dead_letter" if delivery.attempts >= max_attempts else "retry"
    # Exponential retry is capped so a broken sink is retried without a hot loop or overflow.
    delay_seconds = min(3600, 2 ** min(delivery.attempts, 12))
    delivery.next_attempt_at = current + timedelta(seconds=delay_seconds)
    delivery.lease_owner = None
    delivery.lease_expires_at = None
    # Never persist a sink exception verbatim: SDK/network errors can echo credentials or event
    # payload fragments. A stable fingerprint supports correlation without retaining that data.
    fingerprint = sha256(error.encode("utf-8", "replace")).hexdigest()[:16]
    delivery.last_error = f"sink_delivery_failed:{fingerprint}"
    db.commit()
    return True
