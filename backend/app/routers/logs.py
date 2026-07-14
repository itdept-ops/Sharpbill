from fastapi import APIRouter, Depends, Query
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.auth.deps import require_permission
from app.db import get_db
from app.models import RequestLog, User
from app.permissions import LOGS_VIEW
from app.schemas.log import RequestLogListOut, RequestLogOut

router = APIRouter(dependencies=[Depends(require_permission(LOGS_VIEW))])


@router.get("/logs", response_model=RequestLogListOut)
def list_logs(
    db: Session = Depends(get_db),
    limit: int = Query(100, ge=1, le=500),
    offset: int = Query(0, ge=0),
    search: str | None = Query(None),
    user_id: int | None = Query(None),
) -> RequestLogListOut:
    base = select(RequestLog)
    if search:
        base = base.where(RequestLog.path.like(f"%{search}%"))
    if user_id is not None:
        base = base.where(RequestLog.user_id == user_id)
    total = db.scalar(select(func.count()).select_from(base.subquery())) or 0
    rows = list(db.scalars(base.order_by(RequestLog.id.desc()).limit(limit).offset(offset)))

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
    return RequestLogListOut(items=items, total=total)
