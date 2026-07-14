from datetime import UTC, datetime

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.auth import VerifiedIdentity
from app.config import settings
from app.errors import ApiError
from app.models import Role, SiteSettings, User, UserIdentity
from app.permissions import ADMIN_ROLE, DEFAULT_ROLE


def _utcnow() -> datetime:
    return datetime.now(UTC)


def _now_naive() -> datetime:
    # last_seen_at is stored/compared as naive UTC (see app.auth.deps / app.presence).
    return datetime.now(UTC).replace(tzinfo=None)


def get_site_settings(db: Session) -> SiteSettings:
    site = db.get(SiteSettings, 1)
    if site is None:  # pragma: no cover - seeded by migration 0003
        raise ApiError(500, "INTERNAL_ERROR", "Site settings row is missing")
    return site


def _role_by_name(db: Session, name: str) -> Role:
    role = db.scalar(select(Role).where(Role.name == name))
    if role is None:
        role = db.scalar(select(Role).where(Role.name == DEFAULT_ROLE))
    if role is None:  # pragma: no cover - system roles are seeded by migration
        raise ApiError(500, "INTERNAL_ERROR", "Default role is missing")
    return role


def _admin_bootstrap(ident: VerifiedIdentity) -> bool:
    """Whether a first-login identity should be provisioned as admin.

    Only provider-verified identities qualify (unverified Microsoft email claims must not
    grant admin): Google logins (email_verified enforced upstream) or Microsoft logins from
    the configured company tenant.
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


def _assert_login_allowed(ident: VerifiedIdentity) -> None:
    """Optional env allowlist gate (empty config = allow any verified account)."""
    if ident.provider == "google":
        domains = settings.allowed_email_domain_set
        if domains and ident.email.rsplit("@", 1)[-1] not in domains:
            raise ApiError(403, "LOGIN_NOT_ALLOWED", "This account is not permitted to sign in")
    elif ident.provider == "microsoft":
        tenants = settings.allowed_azure_tenant_set
        if tenants and (ident.tenant_id or "") not in tenants:
            raise ApiError(403, "LOGIN_NOT_ALLOWED", "This account is not permitted to sign in")


def _assert_provider_enabled(site: SiteSettings, ident: VerifiedIdentity) -> None:
    if ident.provider == "google" and not site.allow_google:
        raise ApiError(403, "PROVIDER_DISABLED", "Google sign-in is currently disabled")
    if ident.provider == "microsoft" and not site.allow_microsoft:
        raise ApiError(403, "PROVIDER_DISABLED", "Microsoft sign-in is currently disabled")


def _gate_lifecycle(user: User) -> None:
    """The single approval/active gate applied to every login path."""
    if not user.is_approved:
        raise ApiError(403, "PENDING_APPROVAL", "Your account is awaiting administrator approval")
    if not user.is_active:
        raise ApiError(403, "ACCOUNT_DISABLED", "This account has been deactivated")


def find_or_create_user(db: Session, ident: VerifiedIdentity) -> User:
    """Look up by (provider, provider_subject); provision on first login.

    Identity is keyed on the provider's immutable subject id (Google `sub` / Microsoft `oid`),
    NEVER the email — a user changing their provider email cannot become another account, and
    two providers sharing an email are two separate accounts. Provisioning obeys the site's
    signup mode (open / approval / closed) and per-provider toggles.
    """
    _assert_login_allowed(ident)
    site = get_site_settings(db)
    _assert_provider_enabled(site, ident)

    identity = db.scalar(
        select(UserIdentity).where(
            UserIdentity.provider == ident.provider,
            UserIdentity.provider_subject == ident.subject,
        )
    )
    if identity is not None:
        user = identity.user
        _gate_lifecycle(user)
        user.last_login_at = _utcnow()
        user.last_seen_at = _now_naive()
        identity.provider_email = ident.email  # audit trail only; not used for lookups
        db.commit()
        return user

    # --- first login: provision ---
    if site.signup_mode == "closed":
        raise ApiError(403, "SIGNUP_CLOSED", "Sign-ups are currently closed")

    is_admin_boot = _admin_bootstrap(ident)
    if is_admin_boot:
        role = _role_by_name(db, ADMIN_ROLE)
    else:
        role = db.get(Role, site.default_role_id) or _role_by_name(db, DEFAULT_ROLE)
    approved = is_admin_boot or site.signup_mode == "open"

    user = User(
        email=ident.email,
        display_name=ident.display_name,
        role=role,
        is_active=True,
        is_approved=approved,
        last_login_at=_utcnow(),
        last_seen_at=_now_naive(),
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
        _gate_lifecycle(identity.user)  # the race loser must still pass the approval gate
        return identity.user

    if not approved:
        raise ApiError(403, "PENDING_APPROVAL", "Your account was created and is awaiting approval")
    return user


def dev_upsert_user(
    db: Session, email: str, role_name: str | None, display_name: str | None
) -> User:
    """Local dev only: find-or-create a user by email and mint/refresh a 'dev' identity.

    Bypasses provider verification and the approval flow entirely; used exclusively by the
    gated /api/auth/dev route.
    """
    email = email.lower()
    user = db.scalar(select(User).where(User.email == email).order_by(User.id))
    if user is None:
        resolved = role_name or (ADMIN_ROLE if email in settings.admin_email_set else DEFAULT_ROLE)
        user = User(
            email=email,
            display_name=display_name or email.split("@")[0],
            role=_role_by_name(db, resolved),
            is_active=True,
            is_approved=True,
            last_login_at=_utcnow(),
            last_seen_at=_now_naive(),
        )
        db.add(user)
        db.add(
            UserIdentity(user=user, provider="dev", provider_subject=email, provider_email=email)
        )
    else:
        if role_name is not None:
            user.role = _role_by_name(db, role_name)
        if display_name is not None:
            user.display_name = display_name
        user.is_approved = True
        user.last_login_at = _utcnow()
        user.last_seen_at = _now_naive()
        if not any(i.provider == "dev" for i in user.identities):
            db.add(
                UserIdentity(
                    user=user, provider="dev", provider_subject=email, provider_email=email
                )
            )
    db.commit()
    return user
