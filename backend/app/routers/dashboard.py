from datetime import UTC, datetime, timedelta

from fastapi import APIRouter, Depends
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.auth.deps import get_current_user, require_permission
from app.db import get_db
from app.models import Role, User, UserIdentity
from app.permissions import USERS_READ
from app.presence import online_cutoff

router = APIRouter()


def _count(db: Session, *conditions) -> int:
    stmt = select(func.count()).select_from(User)
    for c in conditions:
        stmt = stmt.where(c)
    return db.scalar(stmt) or 0


@router.get("/dashboard")
def dashboard(db: Session = Depends(get_db), user: User = Depends(get_current_user)) -> dict:
    return {
        "stats": {
            "total_users": _count(db),
            "active_users": _count(db, User.is_active.is_(True), User.is_approved.is_(True)),
            "online_users": _count(
                db, User.is_active.is_(True), User.last_seen_at >= online_cutoff()
            ),
        },
    }


@router.get("/dashboard/analytics")
def analytics(
    db: Session = Depends(get_db), _: User = Depends(require_permission(USERS_READ))
) -> dict:
    # Users per role (left join so empty roles show 0).
    role_rows = db.execute(
        select(Role.name, func.count(User.id))
        .select_from(Role)
        .join(User, User.role_id == Role.id, isouter=True)
        .group_by(Role.name)
        .order_by(func.count(User.id).desc())
    ).all()
    roles = [{"role": r[0], "count": int(r[1])} for r in role_rows]

    # Distinct users per auth provider.
    prov_rows = db.execute(
        select(UserIdentity.provider, func.count(func.distinct(UserIdentity.user_id))).group_by(
            UserIdentity.provider
        )
    ).all()
    providers = [{"provider": p[0], "count": int(p[1])} for p in prov_rows]

    # Sign-ups over the last 14 days (zero-filled).
    since = (datetime.now(UTC) - timedelta(days=13)).replace(
        hour=0, minute=0, second=0, microsecond=0, tzinfo=None
    )
    rows = db.execute(
        select(func.date(User.created_at), func.count(User.id))
        .where(User.created_at >= since)
        .group_by(func.date(User.created_at))
    ).all()
    by_date = {str(r[0]): int(r[1]) for r in rows}
    signups = [
        {
            "date": (since + timedelta(days=i)).date().isoformat(),
            "count": by_date.get((since + timedelta(days=i)).date().isoformat(), 0),
        }
        for i in range(14)
    ]

    status = {
        "total": _count(db),
        "active": _count(db, User.is_active.is_(True), User.is_approved.is_(True)),
        "pending": _count(db, User.is_approved.is_(False)),
        "disabled": _count(db, User.is_active.is_(False), User.is_approved.is_(True)),
        "online": _count(db, User.is_active.is_(True), User.last_seen_at >= online_cutoff()),
    }
    return {"roles": roles, "providers": providers, "signups": signups, "status": status}
