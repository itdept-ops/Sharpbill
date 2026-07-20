from fastapi import APIRouter, Depends, Request, Response
from sqlalchemy.orm import Session

from app.account_lifecycle import account_is_authenticatable, lock_current_user
from app.auth.deps import get_current_user, require_permission
from app.config import settings
from app.db import get_db
from app.errors import ApiError
from app.models import SiteSettings, User
from app.permissions import PRIVACY_MANAGE
from app.privacy_lifecycle import (
    AccountErasureError,
    RetentionHoldActive,
    cancel_erasure,
    clear_user_location,
    lock_retention_policy,
    require_no_retention_hold,
    schedule_erasure,
)
from app.schemas.privacy import (
    PrivacyAdminStatusOut,
    PrivacyStatusOut,
    RetentionHoldUpdate,
    RetentionPolicyOut,
)
from app.security_events import add_security_event

router = APIRouter()
admin_router = APIRouter()


def _policy() -> RetentionPolicyOut:
    return RetentionPolicyOut(
        precise_location_hours=settings.precise_location_retention_hours,
        pending_accounts_days=settings.pending_account_retention_days,
        sessions_after_expiry_or_revocation_days=settings.session_retention_days,
        request_activity_days=settings.request_log_retention_days,
        erasure_grace_days=settings.account_erasure_grace_days,
        disabled_accounts_days=settings.disabled_account_retention_days,
        security_events_days=settings.security_event_retention_days,
        legal_acceptances_days=settings.legal_acceptance_retention_days,
    )


def _status(user: User, site: SiteSettings) -> PrivacyStatusOut:
    return PrivacyStatusOut(
        policy=_policy(),
        retention_hold=bool(site.retention_hold),
        erasure_requested_at=user.erasure_requested_at,
        erasure_due_at=user.erasure_due_at,
    )


def _admin_status(site: SiteSettings) -> PrivacyAdminStatusOut:
    return PrivacyAdminStatusOut(
        policy=_policy(),
        retention_hold=bool(site.retention_hold),
        retention_hold_reference=site.retention_hold_reference,
    )


def _locked_user(db: Session, user_id: int) -> User:
    user = lock_current_user(db, user_id)
    if user is None:
        raise ApiError(404, "NOT_FOUND", "User not found")
    return user


def _schedule(
    db: Session,
    user_id: int,
    *,
    request: Request,
    actor_user_id: int,
    requested_by: str,
) -> User:
    # The lifecycle helper locks policy then refresh-locks the account, matching worker order.
    target = db.get(User, user_id)
    if target is None:
        raise ApiError(404, "NOT_FOUND", "User not found")
    try:
        due_at = schedule_erasure(db, target)
    except AccountErasureError as exc:
        raise ApiError(409, "ERASURE_NOT_ALLOWED", str(exc)) from None
    add_security_event(
        db,
        event_type="privacy.erasure.requested",
        outcome="success",
        request=request,
        actor_user_id=actor_user_id,
        target_type="user",
        target_id=target.id,
        metadata={"due_at": due_at.isoformat(), "requested_by": requested_by},
    )
    return target


def _retention_hold_error() -> ApiError:
    return ApiError(
        423,
        "RETENTION_HOLD",
        "Data deletion is suspended by an active retention hold",
    )


@router.get("", response_model=PrivacyStatusOut)
def read_my_privacy_status(
    db: Session = Depends(get_db), user: User = Depends(get_current_user)
) -> PrivacyStatusOut:
    site = db.get(SiteSettings, 1)
    if site is None:
        raise ApiError(500, "SETTINGS_NOT_INITIALIZED", "Site settings are not initialized")
    return _status(user, site)


@router.delete("/location", status_code=204)
def delete_my_saved_location(
    request: Request,
    response: Response,
    db: Session = Depends(get_db),
    user: User = Depends(get_current_user),
) -> Response:
    try:
        require_no_retention_hold(db)
    except RetentionHoldActive:
        raise _retention_hold_error() from None
    target = _locked_user(db, user.id)
    if not account_is_authenticatable(target):
        db.rollback()
        raise ApiError(401, "INVALID_SESSION", "Session invalid or expired")
    changed = clear_user_location(target)
    add_security_event(
        db,
        event_type="privacy.location.cleared",
        outcome="success",
        request=request,
        actor_user_id=target.id,
        target_type="user",
        target_id=target.id,
        metadata={"changed": changed},
    )
    db.commit()
    response.status_code = 204
    return response


@router.post("/erasure-request", response_model=PrivacyStatusOut)
def request_my_erasure(
    request: Request,
    db: Session = Depends(get_db),
    user: User = Depends(get_current_user),
) -> PrivacyStatusOut:
    try:
        target = _schedule(
            db,
            user.id,
            request=request,
            actor_user_id=user.id,
            requested_by="self",
        )
    except RetentionHoldActive:
        raise _retention_hold_error() from None
    db.commit()
    site = db.get(SiteSettings, 1)
    assert site is not None
    return _status(target, site)


@router.delete("/erasure-request", response_model=PrivacyStatusOut)
def cancel_my_erasure(
    request: Request,
    db: Session = Depends(get_db),
    user: User = Depends(get_current_user),
) -> PrivacyStatusOut:
    target = _locked_user(db, user.id)
    try:
        changed = cancel_erasure(target)
    except AccountErasureError as exc:
        raise ApiError(409, "ERASURE_NOT_ALLOWED", str(exc)) from None
    add_security_event(
        db,
        event_type="privacy.erasure.cancelled",
        outcome="success",
        request=request,
        actor_user_id=target.id,
        target_type="user",
        target_id=target.id,
        metadata={"changed": changed},
    )
    db.commit()
    site = db.get(SiteSettings, 1)
    assert site is not None
    return _status(target, site)


@admin_router.get("", response_model=PrivacyAdminStatusOut)
def read_privacy_administration(
    db: Session = Depends(get_db),
    _: User = Depends(require_permission(PRIVACY_MANAGE)),
) -> PrivacyAdminStatusOut:
    site = db.get(SiteSettings, 1)
    if site is None:
        raise ApiError(500, "SETTINGS_NOT_INITIALIZED", "Site settings are not initialized")
    return _admin_status(site)


@admin_router.put("/hold", response_model=PrivacyAdminStatusOut)
def update_retention_hold(
    body: RetentionHoldUpdate,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(PRIVACY_MANAGE)),
) -> PrivacyAdminStatusOut:
    site = lock_retention_policy(db)
    before_enabled = bool(site.retention_hold)
    before_reference = site.retention_hold_reference
    site.retention_hold = body.enabled
    site.retention_hold_reference = body.reference if body.enabled else None
    add_security_event(
        db,
        event_type="privacy.retention_hold.changed",
        outcome="success",
        severity="warning",
        request=request,
        actor_user_id=actor.id,
        target_type="site_settings",
        target_id=site.id,
        metadata={
            "before_enabled": before_enabled,
            "after_enabled": body.enabled,
            "before_reference": before_reference,
            "after_reference": site.retention_hold_reference,
            "reference_changed": before_reference != site.retention_hold_reference,
        },
    )
    db.commit()
    return _admin_status(site)


@admin_router.post("/users/{user_id}/erasure-request", response_model=PrivacyStatusOut)
def schedule_user_erasure(
    user_id: int,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(PRIVACY_MANAGE)),
) -> PrivacyStatusOut:
    if user_id == actor.id:
        raise ApiError(400, "CANNOT_MODIFY_SELF", "Use the personal privacy endpoint")
    try:
        target = _schedule(
            db,
            user_id,
            request=request,
            actor_user_id=actor.id,
            requested_by="administrator",
        )
    except RetentionHoldActive:
        raise _retention_hold_error() from None
    except AccountErasureError as exc:
        raise ApiError(409, "ERASURE_NOT_ALLOWED", str(exc)) from None
    db.commit()
    site = db.get(SiteSettings, 1)
    assert site is not None
    return _status(target, site)


@admin_router.delete("/users/{user_id}/erasure-request", response_model=PrivacyStatusOut)
def cancel_user_erasure(
    user_id: int,
    request: Request,
    db: Session = Depends(get_db),
    actor: User = Depends(require_permission(PRIVACY_MANAGE)),
) -> PrivacyStatusOut:
    target = _locked_user(db, user_id)
    try:
        changed = cancel_erasure(target)
    except AccountErasureError as exc:
        raise ApiError(409, "ERASURE_NOT_ALLOWED", str(exc)) from None
    add_security_event(
        db,
        event_type="privacy.erasure.cancelled",
        outcome="success",
        request=request,
        actor_user_id=actor.id,
        target_type="user",
        target_id=target.id,
        metadata={"changed": changed, "cancelled_by": "administrator"},
    )
    db.commit()
    site = db.get(SiteSettings, 1)
    assert site is not None
    return _status(target, site)
