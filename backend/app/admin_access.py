from sqlalchemy import and_, or_, select
from sqlalchemy.orm import Session

from app.config import settings
from app.models import Role, User, UserIdentity
from app.permissions import ADMIN_ROLE


def active_admin_identity_providers(db: Session, *, lock: bool = False) -> frozenset[str]:
    """Return providers through which an active, approved administrator can authenticate."""
    statement = (
        select(UserIdentity.provider, User.id)
        .join(User, UserIdentity.user_id == User.id)
        .join(Role, User.role_id == Role.id)
        .where(
            Role.name == ADMIN_ROLE,
            User.is_active.is_(True),
            User.is_approved.is_(True),
            # Microsoft login association uses signed (tid, oid). Migration 0018 deliberately
            # moves legacy rows without tid into an unclaimable ``legacy:<id>`` namespace; such
            # rows must not make readiness or last-admin checks report a reachable principal.
            or_(
                and_(
                    UserIdentity.provider.in_(("google", "dev")),
                    UserIdentity.provider_namespace == "",
                ),
                and_(
                    UserIdentity.provider == "microsoft",
                    UserIdentity.provider_tenant_id.is_not(None),
                    UserIdentity.provider_namespace == UserIdentity.provider_tenant_id,
                ),
            ),
        )
    )
    if lock:
        statement = statement.with_for_update()
    return frozenset(row.provider for row in db.execute(statement))


def _bootstrap_identity_available(
    db: Session,
    *,
    provider: str,
    subjects: frozenset[str] | set[str],
    lock: bool,
    required_tenant_id: str | None = None,
) -> bool:
    """Count an unclaimed bootstrap or its still-valid active administrator owner."""
    if not subjects:
        return False
    # Google subjects are global and use the empty namespace. Microsoft object IDs are only
    # unique within the configured bootstrap tenant, so an identical oid in another tenant must
    # neither consume nor satisfy this recovery path.
    namespace = required_tenant_id or ""
    statement = (
        select(
            UserIdentity.provider_subject,
            User.is_active,
            User.is_approved,
            Role.name.label("role_name"),
        )
        .join(User, UserIdentity.user_id == User.id)
        .join(Role, User.role_id == Role.id)
        .where(
            UserIdentity.provider == provider,
            UserIdentity.provider_namespace == namespace,
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
        if owner.role_name == ADMIN_ROLE and owner.is_active and owner.is_approved:
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
        and _bootstrap_identity_available(
            db,
            provider="microsoft",
            subjects=settings.azure_admin_object_id_set,
            required_tenant_id=settings.azure_admin_tenant_id,
            lock=lock,
        )
    )
    return dev or google_bootstrap or microsoft_bootstrap
