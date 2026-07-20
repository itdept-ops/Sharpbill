"""Dev-only login. Mounted only for a local, explicitly enabled, strong-secret configuration."""

import secrets

from fastapi import APIRouter, Depends, Header, Request, Response
from sqlalchemy import select
from sqlalchemy.orm import Session

from app.auth.jwt import set_session_cookie
from app.auth.service import dev_upsert_user
from app.auth.sessions import SessionPrincipalUnavailable, start_session
from app.config import settings
from app.db import get_db
from app.errors import ApiError
from app.models import Role
from app.schemas.auth import DevLoginRequest
from app.schemas.user import UserOut
from app.security_events import add_security_event, commit_security_event

router = APIRouter()


def require_dev_auth_secret(
    supplied: str | None = Header(default=None, alias="X-Dev-Auth-Secret"),
) -> None:
    """Authenticate the local bypass without ever sharing the JWT signing secret."""
    if supplied is None or not secrets.compare_digest(supplied, settings.dev_auth_secret):
        # Hide the bypass surface from unauthenticated callers, even in local mode.
        raise ApiError(404, "NOT_FOUND", "Not found")


@router.post("/dev", response_model=UserOut)
def dev_login(
    body: DevLoginRequest,
    request: Request,
    response: Response,
    _: None = Depends(require_dev_auth_secret),
    db: Session = Depends(get_db),
) -> UserOut:
    user = dev_upsert_user(db, str(body.email), body.role, body.display_name)
    if user.erased_at is not None:
        raise ApiError(403, "ACCOUNT_ERASED", "This account has been erased")
    if not user.is_active:
        raise ApiError(403, "ACCOUNT_DISABLED", "This account has been deactivated")
    if not user.is_approved:
        raise ApiError(403, "PENDING_APPROVAL", "Your account is awaiting administrator approval")
    request.state.user_id = user.id
    add_security_event(
        db,
        event_type="auth.login",
        outcome="success",
        request=request,
        actor_user_id=user.id,
        target_type="user",
        target_id=user.id,
        metadata={"provider": "dev"},
    )
    try:
        token = start_session(db, user.id, request)
    except SessionPrincipalUnavailable as exc:
        db.rollback()
        commit_security_event(
            db,
            event_type="auth.login",
            outcome="denied",
            severity="warning",
            request=request,
            actor_user_id=user.id,
            target_type="user",
            target_id=user.id,
            metadata={"provider": "dev", "reason": exc.code},
        )
        raise ApiError(403, exc.code, exc.message) from None
    set_session_cookie(response, token)
    return UserOut.from_user(user, online=True, include_identity_subjects=True)


@router.get("/dev/roles", response_model=list[str])
def dev_roles(
    _: None = Depends(require_dev_auth_secret), db: Session = Depends(get_db)
) -> list[str]:
    """Role names selectable in the dev-login form (system roles first, then custom)."""
    return [r.name for r in db.scalars(select(Role).order_by(Role.id))]
