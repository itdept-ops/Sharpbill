from sqlalchemy import select
from sqlalchemy.orm import Session

from app.config import settings
from app.models import Role, User, UserIdentity
from app.permissions import ADMIN_ROLE


def _identity_is_currently_admitted(
    provider: str, *, tenant_id: str | None, hosted_domain: str | None
) -> bool:
    """Validate persisted signed organization claims against the current admission policy."""
    if provider == "google":
        domains = settings.allowed_email_domain_set
        return not domains or bool(hosted_domain and hosted_domain.lower() in domains)
    if provider == "microsoft":
        tenants = settings.allowed_azure_tenant_set
        return not tenants or bool(tenant_id and tenant_id in tenants)
    return provider == "dev"


def active_admin_identity_providers(db: Session, *, lock: bool = False) -> frozenset[str]:
    """Return providers through which an active, approved administrator can authenticate."""
    statement = (
        select(
            UserIdentity.provider,
            UserIdentity.provider_tenant_id,
            UserIdentity.provider_hosted_domain,
            User.id,
        )
        .join(User, UserIdentity.user_id == User.id)
        .join(Role, User.role_id == Role.id)
        .where(
            Role.name == ADMIN_ROLE,
            User.is_active.is_(True),
            User.is_approved.is_(True),
        )
    )
    if lock:
        statement = statement.with_for_update()
    return frozenset(
        row.provider
        for row in db.execute(statement)
        if _identity_is_currently_admitted(
            row.provider,
            tenant_id=row.provider_tenant_id,
            hosted_domain=row.provider_hosted_domain,
        )
    )


def _bootstrap_identity_available(
    db: Session,
    *,
    provider: str,
    subjects: frozenset[str] | set[str],
    lock: bool,
) -> bool:
    """Count an unclaimed bootstrap or its still-admitted active administrator owner."""
    if not subjects:
        return False
    statement = (
        select(
            UserIdentity.provider_subject,
            UserIdentity.provider_tenant_id,
            UserIdentity.provider_hosted_domain,
            User.is_active,
            User.is_approved,
            Role.name.label("role_name"),
        )
        .join(User, UserIdentity.user_id == User.id)
        .join(Role, User.role_id == Role.id)
        .where(
            UserIdentity.provider == provider,
            UserIdentity.provider_subject.in_(subjects),
        )
    )
    if lock:
        statement = statement.with_for_update()
    claimed = {row.provider_subject: row for row in db.execute(statement)}
    for subject in subjects:
        owner = claimed.get(subject)
        if owner is None:
            return True
        if (
            owner.role_name == ADMIN_ROLE
            and owner.is_active
            and owner.is_approved
            and _identity_is_currently_admitted(
                provider,
                tenant_id=owner.provider_tenant_id,
                hosted_domain=owner.provider_hosted_domain,
            )
        ):
            return True
    return False


def administration_available(
    db: Session, *, google: bool, microsoft: bool, dev: bool, lock: bool = False
) -> bool:
    effective = {
        provider
        for provider, available in (("google", google), ("microsoft", microsoft), ("dev", dev))
        if available
    }
    if active_admin_identity_providers(db, lock=lock) & effective:
        return True
    google_bootstrap = google and _bootstrap_identity_available(
        db,
        provider="google",
        subjects=settings.google_admin_subject_set,
        lock=lock,
    )
    microsoft_bootstrap = bool(
        microsoft
        and settings.azure_admin_object_id_set
        and settings.azure_admin_tenant_id
        and settings.azure_admin_tenant_id in settings.allowed_azure_tenant_set
        and _bootstrap_identity_available(
            db,
            provider="microsoft",
            subjects=settings.azure_admin_object_id_set,
            lock=lock,
        )
    )
    return dev or google_bootstrap or microsoft_bootstrap
