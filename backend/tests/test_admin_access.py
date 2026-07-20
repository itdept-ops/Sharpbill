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
    monkeypatch.setattr(settings, "allowed_email_domains", "example.com")


def _google_administration_available(db) -> bool:
    return administration_available(db, google=True, microsoft=False, dev=False)


def test_unclaimed_configured_bootstrap_remains_a_recovery_path(db, monkeypatch):
    _configure_google_bootstrap(monkeypatch, "unclaimed-google-subject")

    assert _google_administration_available(db) is True


def test_consumed_bootstrap_identity_is_not_re_elevated_or_counted(db, monkeypatch):
    subject = "consumed-google-subject"
    monkeypatch.setattr(settings, "google_admin_subjects", "")
    monkeypatch.setattr(settings, "allowed_email_domains", "example.com")
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
    user.is_approved = is_approved
    db.commit()

    assert _google_administration_available(db) is False


def test_claimed_google_bootstrap_fails_closed_after_domain_allowlist_drift(db, monkeypatch):
    subject = "google-domain-drift"
    _configure_google_bootstrap(monkeypatch, subject)
    user = find_or_create_user(db, _google(subject))
    assert user.identities[0].provider_hosted_domain == "example.com"
    assert _google_administration_available(db) is True

    monkeypatch.setattr(settings, "allowed_email_domains", "new-example.com")

    assert _google_administration_available(db) is False


def test_claimed_bootstrap_without_legacy_claim_metadata_recovers_on_next_login(db, monkeypatch):
    subject = "legacy-google-bootstrap"
    _configure_google_bootstrap(monkeypatch, subject)
    verified_identity = _google(subject)
    user = find_or_create_user(db, verified_identity)
    stored_identity = user.identities[0]
    stored_identity.provider_hosted_domain = None
    db.commit()

    assert _google_administration_available(db) is False

    find_or_create_user(db, verified_identity)
    db.refresh(stored_identity)
    assert stored_identity.provider_hosted_domain == "example.com"
    assert _google_administration_available(db) is True


def test_claimed_microsoft_bootstrap_fails_closed_after_single_tenant_drift(db, monkeypatch):
    original_tenant = "11111111-1111-4111-8111-111111111111"
    replacement_tenant = "22222222-2222-4222-8222-222222222222"
    subject = "33333333-3333-4333-8333-333333333333"
    monkeypatch.setattr(settings, "azure_admin_tenant_id", original_tenant)
    monkeypatch.setattr(settings, "azure_admin_object_ids", subject)
    monkeypatch.setattr(settings, "allowed_azure_tenants", original_tenant)
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

    # Keep a valid single-tenant bootstrap configuration, but move it to a different tenant.
    monkeypatch.setattr(settings, "azure_admin_tenant_id", replacement_tenant)
    monkeypatch.setattr(settings, "allowed_azure_tenants", replacement_tenant)

    assert administration_available(db, google=False, microsoft=True, dev=False) is False


def test_readiness_rejects_a_consumed_bootstrap_subject(client, db, monkeypatch):
    subject = "readiness-consumed-subject"
    monkeypatch.setattr(settings, "google_admin_subjects", "")
    monkeypatch.setattr(settings, "allowed_email_domains", "example.com")
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
