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

    Only provider-verified identities qualify. Hosted Google deployments use the immutable `sub`
    allowlist. Email bootstrap is retained only as a local-development recovery convenience.
    Microsoft keys on the immutable object id (oid) within the configured admin tenant.
    """
    if ident.provider == "google":
        return ident.subject in settings.google_admin_subject_set or (
            settings.app_env == "local" and ident.email in settings.admin_email_set
        )
    if ident.provider == "microsoft":
        return (
            bool(settings.azure_admin_tenant_id)
            and ident.tenant_id == settings.azure_admin_tenant_id
            and ident.subject in settings.azure_admin_object_id_set
        )
    return False


def _assert_login_allowed(ident: VerifiedIdentity) -> None:
    """Enforce provider-issued organization claims when an allowlist is configured."""
    if ident.provider == "google":
        domains = settings.allowed_email_domain_set
        # Google explicitly warns that an email suffix does not establish Workspace membership;
        # only the signed `hd` claim is authoritative for an organization restriction.
        if domains and (ident.hosted_domain or "").lower() not in domains:
            raise ApiError(403, "LOGIN_NOT_ALLOWED", "This account is not permitted to sign in")
    elif ident.provider == "microsoft":
        tenants = settings.allowed_azure_tenant_set
        if tenants and (ident.tenant_id or "") not in tenants:
            raise ApiError(403, "LOGIN_NOT_ALLOWED", "This account is not permitted to sign in")


def _assert_new_account_admission(ident: VerifiedIdentity, *, is_admin_boot: bool) -> None:
    """Require an organization allowlist or an explicit public-signup acknowledgement."""
    if is_admin_boot or settings.allow_public_signup:
        return
    has_provider_allowlist = (
        bool(settings.allowed_email_domain_set)
        if ident.provider == "google"
        else bool(settings.allowed_azure_tenant_set)
    )
    if not has_provider_allowlist:
        raise ApiError(
            403,
            "SIGNUP_RESTRICTED",
            "New accounts require an organization allowlist or explicit public signup",
        )


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
        # Persist only claims that survived provider signature and organization-admission checks.
        # Readiness uses these immutable authority claims rather than trusting an email suffix.
        identity.provider_tenant_id = ident.tenant_id
        identity.provider_hosted_domain = ident.hosted_domain
        db.commit()
        return user

    # --- first login: provision ---
    # An immutable Google sub or Microsoft (tenant, oid) bootstrap identity may enter even when
    # signup is closed, preserving a controlled recovery/seed path. Everyone else obeys it.
    is_admin_boot = _admin_bootstrap(ident)
    _assert_new_account_admission(ident, is_admin_boot=is_admin_boot)
    if site.signup_mode == "closed" and not is_admin_boot:
        raise ApiError(403, "SIGNUP_CLOSED", "Sign-ups are currently closed")

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
            provider_tenant_id=ident.tenant_id,
            provider_hosted_domain=ident.hosted_domain,
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
        # The dev seam may authenticate an existing account, but must never rewrite its role,
        # profile, or approval state from caller-controlled fields. Inactive/pending accounts are
        # returned unchanged so the router can reject them without a committed side effect.
        if not user.is_active or not user.is_approved:
            return user
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
