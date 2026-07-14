from fastapi import APIRouter, Depends
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.auth.deps import get_current_user, require_permission
from app.db import get_db
from app.models import User
from app.permissions import PRESENCE_VIEW
from app.presence import ONLINE_WINDOW_SECONDS, online_cutoff
from app.schemas.presence import PresenceOut, PresenceUser

router = APIRouter()


@router.get("/online", response_model=PresenceOut)
def online_users(
    db: Session = Depends(get_db), _: User = Depends(require_permission(PRESENCE_VIEW))
) -> PresenceOut:
    cutoff = online_cutoff()
    users = list(
        db.scalars(
            select(User)
            .where(
                User.is_active.is_(True),
                User.last_seen_at.is_not(None),
                User.last_seen_at >= cutoff,
            )
            .order_by(User.last_seen_at.desc())
        )
    )
    return PresenceOut(
        online=[
            PresenceUser(
                id=u.id,
                email=u.email,
                display_name=u.display_name,
                role=u.role_name,
                last_seen_at=u.last_seen_at,
            )
            for u in users
        ],
        count=len(users),
        window_seconds=ONLINE_WINDOW_SECONDS,
    )


@router.post("/heartbeat")
def heartbeat(user: User = Depends(get_current_user)) -> dict:
    """Any authenticated caller can ping to stay 'online'. get_current_user bumps last_seen."""
    return {"ok": True, "user_id": user.id}
