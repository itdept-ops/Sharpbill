from datetime import UTC, datetime

import pytest
from sqlalchemy import select

from app.admin_access import administration_available
from app.auth import VerifiedIdentity
from app.auth.service import find_or_create_user
from app.config import settings
from app.models import Role


def _google(subject: str, *, domain: str = "example.com") -> VerifiedIdentity:
    return VerifiedIdentity(
        provider="google",
        subject=subject,
        email=f"admin@{domain}",
        display_name="Recovery Admin",
        hosted_domain=domain,
    )


def _configure_google_bootstrap(monkeypatch, subject: str) -> None:
    monkeypatch.setattr(settings, "google_admin_subjects", subject)


def _google_administration_available(db) -> bool:
    return administration_available(db, google=True, microsoft=False, dev=False)


def test_unclaimed_configured_bootstrap_remains_a_recovery_path(db, monkeypatch):
    _configure_google_bootstrap(monkeypatch, "unclaimed-google-subject")

    assert _google_administration_available(db) is True


def test_consumed_bootstrap_identity_is_not_re_elevated_or_counted(db, monkeypatch):
    subject = "consumed-google-subject"
    monkeypatch.setattr(settings, "google_admin_subjects", "")
    identity = _google(subject)
    user = find_or_create_user(db, identity)
    assert user.role_name == "user"

    # Adding a previously consumed subject to the bootstrap config cannot make the existing-user
    # login branch a recovery path: that branch intentionally never rewrites access.
    monkeypatch.setattr(settings, "google_admin_subjects", subject)
    same_user = find_or_create_user(db, identity)

    assert same_user.id == user.id
    assert same_user.role_name == "user"
    assert _google_administration_available(db) is False


@pytest.mark.parametrize(
    ("role_name", "is_active", "is_approved"),
    [
        pytest.param("user", True, True, id="demoted"),
        pytest.param("admin", False, True, id="inactive"),
        pytest.param("admin", True, False, id="pending"),
    ],
)
def test_claimed_bootstrap_requires_an_active_approved_admin_owner(
    db, monkeypatch, role_name, is_active, is_approved
):
    subject = f"claimed-{role_name}-{is_active}-{is_approved}"
    _configure_google_bootstrap(monkeypatch, subject)
    user = find_or_create_user(db, _google(subject))
    assert user.role_name == "admin"

    user.role = db.scalar(select(Role).where(Role.name == role_name))
    user.is_active = is_active
    user.deactivated_at = None if is_active else datetime.now(UTC).replace(tzinfo=None)
    user.is_approved = is_approved
    db.commit()

    assert _google_administration_available(db) is False


def test_claimed_google_bootstrap_remains_valid_when_provider_context_changes(db, monkeypatch):
    subject = "google-context-change"
    _configure_google_bootstrap(monkeypatch, subject)
    user = find_or_create_user(db, _google(subject))
    assert user.identities[0].provider_hosted_domain == "example.com"
    assert _google_administration_available(db) is True

    find_or_create_user(db, _google(subject, domain="new-example.com"))

    assert user.identities[0].provider_hosted_domain == "new-example.com"
    assert _google_administration_available(db) is True


def test_claimed_google_bootstrap_does_not_depend_on_hosted_domain_metadata(db, monkeypatch):
    subject = "legacy-google-bootstrap"
    _configure_google_bootstrap(monkeypatch, subject)
    verified_identity = _google(subject)
    user = find_or_create_user(db, verified_identity)
    stored_identity = user.identities[0]
    stored_identity.provider_hosted_domain = None
    db.commit()

    assert _google_administration_available(db) is True

    find_or_create_user(db, verified_identity)
    db.refresh(stored_identity)
    assert stored_identity.provider_hosted_domain == "example.com"
    assert _google_administration_available(db) is True


def test_noncanonical_google_namespace_does_not_satisfy_recovery(db, monkeypatch):
    subject = "noncanonical-google-admin"
    _configure_google_bootstrap(monkeypatch, subject)
    user = find_or_create_user(db, _google(subject))
    identity = user.identities[0]
    identity.provider_namespace = "unclaimable-namespace"
    db.commit()
    _configure_google_bootstrap(monkeypatch, "")

    assert _google_administration_available(db) is False


def test_claimed_microsoft_admin_remains_reachable_after_bootstrap_config_changes(db, monkeypatch):
    original_tenant = "11111111-1111-4111-8111-111111111111"
    replacement_tenant = "22222222-2222-4222-8222-222222222222"
    subject = "33333333-3333-4333-8333-333333333333"
    monkeypatch.setattr(settings, "azure_admin_tenant_id", original_tenant)
    monkeypatch.setattr(settings, "azure_admin_object_ids", subject)
    user = find_or_create_user(
        db,
        VerifiedIdentity(
            provider="microsoft",
            subject=subject,
            email="admin@example.com",
            display_name="Recovery Admin",
            tenant_id=original_tenant,
        ),
    )
    assert user.identities[0].provider_tenant_id == original_tenant
    assert administration_available(db, google=False, microsoft=True, dev=False) is True

    # Moving the bootstrap authority affects only future bootstrap claims. It is not an IdP
    # tenant allowlist, so the existing administrator remains reachable through Microsoft.
    monkeypatch.setattr(settings, "azure_admin_tenant_id", replacement_tenant)

    assert administration_available(db, google=False, microsoft=True, dev=False) is True


def test_microsoft_bootstrap_is_scoped_to_the_configured_tenant(db, monkeypatch):
    configured_tenant = "11111111-1111-4111-8111-111111111111"
    other_tenant = "22222222-2222-4222-8222-222222222222"
    shared_oid = "33333333-3333-4333-8333-333333333333"
    monkeypatch.setattr(settings, "azure_admin_tenant_id", "")
    monkeypatch.setattr(settings, "azure_admin_object_ids", "")
    other_user = find_or_create_user(
        db,
        VerifiedIdentity(
            provider="microsoft",
            subject=shared_oid,
            email="other-tenant@example.com",
            display_name="Other tenant member",
            tenant_id=other_tenant,
        ),
    )
    assert other_user.role_name == "user"

    monkeypatch.setattr(settings, "azure_admin_tenant_id", configured_tenant)
    monkeypatch.setattr(settings, "azure_admin_object_ids", shared_oid)
    assert administration_available(db, google=False, microsoft=True, dev=False) is True

    configured_admin = find_or_create_user(
        db,
        VerifiedIdentity(
            provider="microsoft",
            subject=shared_oid,
            email="configured-tenant@example.com",
            display_name="Configured tenant admin",
            tenant_id=configured_tenant,
        ),
    )
    assert configured_admin.id != other_user.id
    assert configured_admin.role_name == "admin"


def test_unclaimable_legacy_microsoft_admin_does_not_satisfy_recovery(db, monkeypatch):
    tenant = "11111111-1111-4111-8111-111111111111"
    subject = "33333333-3333-4333-8333-333333333333"
    monkeypatch.setattr(settings, "azure_admin_tenant_id", tenant)
    monkeypatch.setattr(settings, "azure_admin_object_ids", subject)
    user = find_or_create_user(
        db,
        VerifiedIdentity(
            provider="microsoft",
            subject=subject,
            email="legacy-admin@example.com",
            display_name="Legacy admin",
            tenant_id=tenant,
        ),
    )
    identity = user.identities[0]
    identity.provider_namespace = f"legacy:{identity.id}"
    identity.provider_tenant_id = None
    db.commit()
    monkeypatch.setattr(settings, "azure_admin_tenant_id", "")
    monkeypatch.setattr(settings, "azure_admin_object_ids", "")

    assert administration_available(db, google=False, microsoft=True, dev=False) is False


def test_readiness_rejects_a_consumed_bootstrap_subject(client, db, monkeypatch):
    subject = "readiness-consumed-subject"
    monkeypatch.setattr(settings, "google_admin_subjects", "")
    user = find_or_create_user(db, _google(subject))
    assert user.role_name == "user"

    monkeypatch.setattr(settings, "app_env", "production")
    monkeypatch.setattr(settings, "dev_auth_enabled", False)
    monkeypatch.setattr(
        settings, "google_client_id", "123456-testclient.apps.googleusercontent.com"
    )
    monkeypatch.setattr(settings, "azure_client_id", "")
    monkeypatch.setattr(settings, "google_admin_subjects", subject)

    response = client.get("/api/health/ready")

    assert response.status_code == 503
    assert response.json()["administration"] == "unavailable"
