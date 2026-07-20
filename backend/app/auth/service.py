from datetime import UTC, datetime

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from app.account_lifecycle import lock_current_user
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


def get_site_settings(db: Session, *, lock_policy: bool = False) -> SiteSettings:
    statement = select(SiteSettings).where(SiteSettings.id == 1)
    if lock_policy:
        # Shared policy locks let logins proceed concurrently while serializing admission,
        # provider-toggle, default-role, and retention-policy transitions around provisioning.
        statement = statement.with_for_update(read=True)
    site = db.scalar(statement.execution_options(populate_existing=True))
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


def _assert_provider_enabled(site: SiteSettings, ident: VerifiedIdentity) -> None:
    if ident.provider == "google" and not site.allow_google:
        raise ApiError(403, "PROVIDER_DISABLED", "Google sign-in is currently disabled")
    if ident.provider == "microsoft" and not site.allow_microsoft:
        raise ApiError(403, "PROVIDER_DISABLED", "Microsoft sign-in is currently disabled")


def _gate_lifecycle(user: User) -> None:
    """The single approval/active gate applied to every login path."""
    if user.erased_at is not None:
        raise ApiError(403, "ACCOUNT_ERASED", "This account has been erased")
    if not user.is_approved:
        raise ApiError(403, "PENDING_APPROVAL", "Your account is awaiting administrator approval")
    if not user.is_active:
        raise ApiError(403, "ACCOUNT_DISABLED", "This account has been deactivated")


def _identity_namespace(ident: VerifiedIdentity) -> str:
    """Return the authority namespace that makes an immutable provider subject unique."""
    if ident.provider != "microsoft":
        return ""
    if not ident.tenant_id:
        raise ApiError(401, "INVALID_IDENTITY", "Microsoft identity is missing its tenant")
    return ident.tenant_id


def _identity_query(ident: VerifiedIdentity):
    return select(UserIdentity).where(
        UserIdentity.provider == ident.provider,
        UserIdentity.provider_namespace == _identity_namespace(ident),
        UserIdentity.provider_subject == ident.subject,
    )


def find_or_create_user(db: Session, ident: VerifiedIdentity) -> User:
    """Look up by the provider's immutable, authority-scoped key; provision on first login.

    Google uses its globally scoped ``sub``. Microsoft uses signed ``(tid, oid)`` because object
    IDs are tenant-scoped. Identity is keyed on these immutable identifiers,
    NEVER the email — a user changing their provider email cannot become another account, and
    two providers sharing an email are two separate accounts. Provisioning obeys the site's
    signup mode (open / approval / closed) and per-provider toggles.
    """
    site = get_site_settings(db, lock_policy=True)
    _assert_provider_enabled(site, ident)

    identity = db.scalar(_identity_query(ident))
    if identity is not None:
        # A retention worker can anonymize this principal after the identity lookup. Use a
        # current locking read (and overwrite any REPEATABLE READ identity-map snapshot) before
        # gating or restoring login timestamps, so PII cannot be written back after erasure.
        user = lock_current_user(db, identity.user_id)
        if user is None:  # protected by the identity FK; fail closed if storage is corrupted
            raise ApiError(403, "ACCOUNT_DISABLED", "This account is unavailable")
        _gate_lifecycle(user)
        user.last_login_at = _utcnow()
        user.last_seen_at = _now_naive()
        # Retain claims that survived provider signature verification as bounded audit context.
        # They do not grant access; provider state, account lifecycle, signup mode, and RBAC do.
        identity.provider_tenant_id = ident.tenant_id
        identity.provider_hosted_domain = ident.hosted_domain
        db.commit()
        return user

    # --- first login: provision ---
    # An immutable Google sub or Microsoft (tenant, oid) bootstrap identity may enter even when
    # signup is closed, preserving a controlled recovery/seed path. Everyone else obeys it.
    is_admin_boot = _admin_bootstrap(ident)
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
            provider_namespace=_identity_namespace(ident),
            provider_subject=ident.subject,
            provider_tenant_id=ident.tenant_id,
            provider_hosted_domain=ident.hosted_domain,
        )
    )
    try:
        db.commit()
    except IntegrityError:  # two concurrent first logins raced
        db.rollback()
        identity = db.scalar(_identity_query(ident))
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
    dev_identity = db.scalar(
        select(UserIdentity).where(
            UserIdentity.provider == "dev",
            UserIdentity.provider_namespace == "",
            UserIdentity.provider_subject == email,
        )
    )
    # Retained identity markers suppress transparent re-provisioning after erasure. The email
    # fallback preserves the local convenience of attaching a dev identity to an existing user.
    user_id = (
        dev_identity.user_id
        if dev_identity is not None
        else db.scalar(select(User.id).where(User.email == email).order_by(User.id))
    )
    user = lock_current_user(db, user_id) if user_id is not None else None
    if user_id is not None and user is None:  # protected by FK; fail closed on corrupt storage
        raise ApiError(403, "ACCOUNT_DISABLED", "This account is unavailable")
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
            UserIdentity(
                user=user,
                provider="dev",
                provider_namespace="",
                provider_subject=email,
            )
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
                    user=user,
                    provider="dev",
                    provider_namespace="",
                    provider_subject=email,
                )
            )
    db.commit()
    return user
