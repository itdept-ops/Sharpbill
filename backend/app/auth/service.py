from datetime import UTC, datetime

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.auth import VerifiedIdentity
from app.config import settings
from app.errors import ApiError
from app.models import User, UserIdentity


def _utcnow() -> datetime:
    return datetime.now(UTC)


def _admin_bootstrap(ident: VerifiedIdentity) -> bool:
    """Whether a first-login identity should be provisioned as admin.

    Only provider-verified identities qualify (decision: unverified Microsoft email claims
    must not grant admin): Google logins (email_verified is enforced upstream) or Microsoft
    logins from the configured company tenant.
    """
    if ident.email not in settings.admin_email_set:
        return False
    if ident.provider == "google":
        return True
    if ident.provider == "microsoft":
        return (
            bool(settings.azure_admin_tenant_id)
            and ident.tenant_id == settings.azure_admin_tenant_id
        )
    return False


def find_or_create_user(db: Session, ident: VerifiedIdentity) -> User:
    """Look up by (provider, provider_subject); provision on first login.

    Never links accounts by email. Same email via two providers = two accounts.
    """
    identity = db.scalar(
        select(UserIdentity).where(
            UserIdentity.provider == ident.provider,
            UserIdentity.provider_subject == ident.subject,
        )
    )
    if identity is not None:
        user = identity.user
        if not user.is_active:
            raise ApiError(403, "ACCOUNT_DISABLED", "This account has been deactivated")
        user.last_login_at = _utcnow()
        identity.provider_email = ident.email
        db.commit()
        return user

    role = "admin" if _admin_bootstrap(ident) else "user"
    user = User(
        email=ident.email,
        display_name=ident.display_name,
        role=role,
        is_active=True,
        last_login_at=_utcnow(),
    )
    db.add(user)
    db.add(
        UserIdentity(
            user=user,
            provider=ident.provider,
            provider_subject=ident.subject,
            provider_email=ident.email,
        )
    )
    try:
        db.commit()
    except IntegrityError:  # two concurrent first logins raced
        db.rollback()
        identity = db.scalar(
            select(UserIdentity).where(
                UserIdentity.provider == ident.provider,
                UserIdentity.provider_subject == ident.subject,
            )
        )
        if identity is None:
            raise
        return identity.user
    return user


def dev_upsert_user(db: Session, email: str, role: str | None, display_name: str | None) -> User:
    """Local dev only: find-or-create a user by email and mint/refresh a 'dev' identity.

    Bypasses provider verification entirely; used exclusively by the gated /api/auth/dev route.
    """
    email = email.lower()
    user = db.scalar(select(User).where(User.email == email).order_by(User.id))
    if user is None:
        resolved_role = role or ("admin" if email in settings.admin_email_set else "user")
        user = User(
            email=email,
            display_name=display_name or email.split("@")[0],
            role=resolved_role,
            is_active=True,
            last_login_at=_utcnow(),
        )
        db.add(user)
        db.add(
            UserIdentity(user=user, provider="dev", provider_subject=email, provider_email=email)
        )
    else:
        if role is not None:
            user.role = role
        if display_name is not None:
            user.display_name = display_name
        user.last_login_at = _utcnow()
        has_dev = any(i.provider == "dev" for i in user.identities)
        if not has_dev:
            db.add(
                UserIdentity(
                    user=user, provider="dev", provider_subject=email, provider_email=email
                )
            )
    db.commit()
    return user
