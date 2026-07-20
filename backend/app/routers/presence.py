from fastapi import APIRouter, Depends
from sqlalchemy import func, select
from sqlalchemy.orm import Session, selectinload

from app.auth.deps import get_current_user, require_permission
from app.db import get_db
from app.models import User
from app.permissions import PRESENCE_VIEW
from app.presence import ONLINE_WINDOW_SECONDS, online_cutoff
from app.schemas.presence import PresenceOut, PresenceUser

router = APIRouter()

# A large tenant must not turn one polling request into an unbounded ORM allocation or response.
# Clients still receive the exact count and explicit truncation metadata.
_PRESENCE_ROSTER_LIMIT = 500


@router.get("/online", response_model=PresenceOut)
def online_users(
    db: Session = Depends(get_db), _: User = Depends(require_permission(PRESENCE_VIEW))
) -> PresenceOut:
    cutoff = online_cutoff()
    eligible = (
        User.is_active.is_(True),
        User.is_approved.is_(True),
        User.last_seen_at.is_not(None),
        User.last_seen_at >= cutoff,
    )
    count = db.scalar(select(func.count(User.id)).where(*eligible)) or 0
    users = list(
        db.scalars(
            select(User)
            .options(selectinload(User.role))
            .where(*eligible)
            .order_by(User.last_seen_at.desc(), User.id.desc())
            .limit(_PRESENCE_ROSTER_LIMIT)
        )
    )
    return PresenceOut(
        online=[
            PresenceUser(
                id=u.id,
                display_name=u.display_name,
                role=u.role_name,
                last_seen_at=u.last_seen_at,
            )
            for u in users
        ],
        count=count,
        window_seconds=ONLINE_WINDOW_SECONDS,
        truncated=count > len(users),
        roster_limit=_PRESENCE_ROSTER_LIMIT,
    )


@router.post("/heartbeat")
def heartbeat(user: User = Depends(get_current_user)) -> dict:
    """Any authenticated caller can ping to stay 'online'. get_current_user bumps last_seen."""
    return {"ok": True, "user_id": user.id}
