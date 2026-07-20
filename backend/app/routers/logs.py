from fastapi import APIRouter, Depends, Query
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.auth.deps import require_permission
from app.db import get_db
from app.models import RequestLog, User
from app.permissions import LOGS_VIEW
from app.request_logging import flush_request_logging, request_logging_metrics
from app.schemas.log import RequestLogListOut, RequestLogOut
from app.sqlutil import escape_like

router = APIRouter(dependencies=[Depends(require_permission(LOGS_VIEW))])


@router.get("/logs/metrics")
def log_pipeline_metrics() -> dict[str, int | bool]:
    """Bounded-writer counters for monitoring loss and backpressure."""
    return request_logging_metrics()


@router.get("/logs", response_model=RequestLogListOut)
def list_logs(
    db: Session = Depends(get_db),
    limit: int = Query(100, ge=1, le=500),
    offset: int = Query(0, ge=0, le=10_000),
    before_id: int | None = Query(None, ge=1),
    search: str | None = Query(None, max_length=100),
    method: str | None = Query(None, max_length=10),
    user_id: int | None = Query(None),
) -> RequestLogListOut:
    # Persistence is intentionally eventual. An operator opening the log view gets a bounded
    # barrier so every row accepted before this request is visible in the result.
    flush_request_logging(timeout=2.0)
    base = select(RequestLog)
    if search:
        base = base.where(RequestLog.path.like(f"%{escape_like(search)}%", escape="\\"))
    if method:
        base = base.where(RequestLog.method == method.upper())
    if user_id is not None:
        base = base.where(RequestLog.user_id == user_id)
    total = db.scalar(select(func.count()).select_from(base.subquery())) or 0
    page = base
    if before_id is not None:
        page = page.where(RequestLog.id < before_id)
    # Fetch one sentinel row so callers can advance with an immutable keyset cursor. ``offset``
    # remains for existing clients but is capped to prevent pathological deep scans.
    rows = list(db.scalars(page.order_by(RequestLog.id.desc()).limit(limit + 1).offset(offset)))
    has_more = len(rows) > limit
    rows = rows[:limit]

    emails: dict[int, str] = {}
    uids = {r.user_id for r in rows if r.user_id is not None}
    if uids:
        for u in db.scalars(select(User).where(User.id.in_(uids))):
            emails[u.id] = u.email

    items = [
        RequestLogOut(
            id=r.id,
            method=r.method,
            path=r.path,
            user_id=r.user_id,
            user_email=emails.get(r.user_id) if r.user_id is not None else None,
            ip=r.ip,
            status_code=r.status_code,
            created_at=r.created_at,
        )
        for r in rows
    ]
    return RequestLogListOut(
        items=items,
        total=total,
        next_cursor=items[-1].id if has_more and items else None,
    )
