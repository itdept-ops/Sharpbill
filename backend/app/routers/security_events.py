import csv
import io
import json
from collections.abc import Iterable

from fastapi import APIRouter, Depends, Query, Request
from fastapi.responses import StreamingResponse
from sqlalchemy import Select, select
from sqlalchemy.orm import Session

from app.auth.deps import require_permission
from app.db import get_db
from app.models import SecurityEvent, SecurityEventDelivery, User
from app.permissions import SECURITY_EVENTS_VIEW
from app.schemas.security_event import SecurityEventListOut, SecurityEventOut
from app.security_events import add_security_event

_require_security_events_view = require_permission(SECURITY_EVENTS_VIEW)
router = APIRouter(dependencies=[Depends(_require_security_events_view)])


def _filtered(
    *,
    before_id: int | None,
    event_type: str | None,
    outcome: str | None,
    actor_user_id: int | None,
    request_id: str | None,
) -> Select:
    statement = select(SecurityEvent, SecurityEventDelivery).join(
        SecurityEventDelivery, SecurityEventDelivery.event_id == SecurityEvent.id
    )
    if before_id is not None:
        statement = statement.where(SecurityEvent.id < before_id)
    if event_type is not None:
        statement = statement.where(SecurityEvent.event_type == event_type)
    if outcome is not None:
        statement = statement.where(SecurityEvent.outcome == outcome)
    if actor_user_id is not None:
        statement = statement.where(SecurityEvent.actor_user_id == actor_user_id)
    if request_id is not None:
        statement = statement.where(SecurityEvent.request_id == request_id)
    return statement.order_by(SecurityEvent.id.desc())


def _out(event: SecurityEvent, delivery: SecurityEventDelivery) -> SecurityEventOut:
    return SecurityEventOut(
        id=event.id,
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
        retention_until=event.retention_until,
        delivery_status=delivery.status,
        delivery_attempts=delivery.attempts,
        delivered_at=delivery.delivered_at,
    )


@router.get("/security-events", response_model=SecurityEventListOut)
def list_security_events(
    db: Session = Depends(get_db),
    limit: int = Query(100, ge=1, le=500),
    before_id: int | None = Query(None, ge=1),
    event_type: str | None = Query(None, min_length=3, max_length=80),
    outcome: str | None = Query(None, pattern="^(success|failure|denied)$"),
    actor_user_id: int | None = Query(None, ge=1),
    request_id: str | None = Query(None, min_length=1, max_length=64),
) -> SecurityEventListOut:
    """Read a bounded cursor page; immutable IDs prevent offset drift under active writes."""
    rows = db.execute(
        _filtered(
            before_id=before_id,
            event_type=event_type,
            outcome=outcome,
            actor_user_id=actor_user_id,
            request_id=request_id,
        ).limit(limit + 1)
    ).all()
    has_more = len(rows) > limit
    visible = rows[:limit]
    items = [_out(event, delivery) for event, delivery in visible]
    return SecurityEventListOut(
        items=items,
        next_cursor=items[-1].id if has_more and items else None,
    )


def _csv_rows(rows: Iterable[tuple[SecurityEvent, SecurityEventDelivery]]) -> Iterable[str]:
    buffer = io.StringIO()
    writer = csv.writer(buffer)

    def flush() -> str:
        data = buffer.getvalue()
        buffer.seek(0)
        buffer.truncate(0)
        return data

    writer.writerow(
        [
            "id",
            "occurred_at",
            "event_type",
            "outcome",
            "severity",
            "request_id",
            "actor_user_id",
            "target_type",
            "target_id",
            "source_ip",
            "metadata_json",
            "delivery_status",
            "delivery_attempts",
            "delivered_at",
            "retention_until",
        ]
    )
    yield flush()
    for event, delivery in rows:
        writer.writerow(
            [
                event.id,
                event.occurred_at.isoformat(),
                event.event_type,
                event.outcome,
                event.severity,
                event.request_id or "",
                event.actor_user_id or "",
                event.target_type or "",
                event.target_id or "",
                event.source_ip or "",
                json.dumps(event.event_metadata, sort_keys=True, separators=(",", ":")),
                delivery.status,
                delivery.attempts,
                delivery.delivered_at.isoformat() if delivery.delivered_at else "",
                event.retention_until.isoformat(),
            ]
        )
        yield flush()


@router.get("/security-events/export.csv")
def export_security_events(
    request: Request,
    db: Session = Depends(get_db),
    limit: int = Query(1000, ge=1, le=10_000),
    before_id: int | None = Query(None, ge=1),
    event_type: str | None = Query(None, min_length=3, max_length=80),
    outcome: str | None = Query(None, pattern="^(success|failure|denied)$"),
    actor_user_id: int | None = Query(None, ge=1),
    request_id: str | None = Query(None, min_length=1, max_length=64),
    actor: User = Depends(_require_security_events_view),
) -> StreamingResponse:
    """Export at most 10,000 filtered events per cursor window."""
    result_rows = db.execute(
        _filtered(
            before_id=before_id,
            event_type=event_type,
            outcome=outcome,
            actor_user_id=actor_user_id,
            request_id=request_id,
        ).limit(limit)
    ).all()
    rows = [(event, delivery) for event, delivery in result_rows]
    add_security_event(
        db,
        event_type="security_events.exported",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="security_event_collection",
        metadata={
            "exported_count": len(rows),
            "limit": limit,
            "filters": {
                "before_id": before_id,
                "event_type": event_type,
                "outcome": outcome,
                "actor_user_id": actor_user_id,
                "request_id": request_id,
            },
        },
    )
    db.commit()
    return StreamingResponse(
        _csv_rows(rows),
        media_type="text/csv",
        headers={"Content-Disposition": "attachment; filename=security-events.csv"},
    )
