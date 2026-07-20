from datetime import UTC, datetime, timedelta

import pytest
from fastapi.testclient import TestClient as RawTestClient
from pydantic import ValidationError
from sqlalchemy import func, select
from sqlalchemy.exc import DBAPIError

from app.auth import VerifiedIdentity
from app.auth.service import find_or_create_user
from app.config import settings
from app.legal_acceptance import CURRENT_LEGAL_BUNDLE_VERSION
from app.main import app
from app.models import LegalAcceptance, SecurityEvent, SiteSettings, User, UserSession
from app.privacy_lifecycle import anonymize_user
from app.retention import run_retention_cycle
from app.schemas.legal import LegalDocumentOut
from tests.client import DEV_AUTH_HEADERS


def _payload(email: str = "legal@example.com") -> dict:
    return {
        "email": email,
        "role": "user",
        "legal_accepted": True,
        "legal_bundle_version": CURRENT_LEGAL_BUNDLE_VERSION,
    }


def _historic_acceptance(
    *, user_id: int, accepted_at: datetime, retention_until: datetime, bundle: str
):
    digest = "a" * 64
    return LegalAcceptance(
        user_id=user_id,
        bundle_version=bundle,
        terms_version="historic",
        eula_version="historic",
        acceptable_use_version="historic",
        privacy_version="historic",
        terms_sha256=digest,
        eula_sha256=digest,
        acceptable_use_sha256=digest,
        privacy_sha256=digest,
        accepted_at=accepted_at,
        retention_until=retention_until,
    )


def test_public_manifest_is_the_exact_versioned_login_contract(client):
    response = client.get("/api/legal/manifest")
    assert response.status_code == 200
    assert response.json() == {
        "bundle_version": "2026-07-20-v1",
        "effective_date": "2026-07-20",
        "required_at_login": True,
        "acceptance_label": (
            "I agree to the Terms of Service, EULA, and Acceptable Use Policy, and acknowledge "
            "the Privacy Notice."
        ),
        "documents": [
            {
                "key": "terms",
                "title": "Terms of Service",
                "version": "2026-07-20-v1",
                "sha256": "2c77250037d037141e79fd11f1a85cde1e9257d51cb325e7fdaefa6cf4f0ff2e",
                "url": "/legal/terms-of-service.html",
                "acceptance": "agreement",
            },
            {
                "key": "eula",
                "title": "End User License Agreement",
                "version": "2026-07-20-v1",
                "sha256": "16bd045a449990e3f7325f0d67d81d4fee54f679ec53164835f0c19725e25638",
                "url": "/legal/eula.html",
                "acceptance": "agreement",
            },
            {
                "key": "acceptable_use",
                "title": "Acceptable Use Policy",
                "version": "2026-07-20-v1",
                "sha256": "d4391a0abe57885964606521039a4cca0151f8e11d95c628efc51b603eefdb0d",
                "url": "/legal/acceptable-use-policy.html",
                "acceptance": "agreement",
            },
            {
                "key": "privacy",
                "title": "Privacy Notice",
                "version": "2026-07-20-v1",
                "sha256": "fb96f77cc9846282c9555105994d0dc9b400c2a6eaf35e15b390b5a3c5db2d3d",
                "url": "/legal/privacy-notice.html",
                "acceptance": "acknowledgement",
            },
        ],
    }


def test_manifest_schema_rejects_noncanonical_digest():
    with pytest.raises(ValidationError):
        LegalDocumentOut(
            key="terms",
            title="Terms of Service",
            version="v1",
            sha256="A" * 64,
            url="/legal/terms-of-service.html",
            acceptance="agreement",
        )


def test_login_rejects_missing_unchecked_and_stale_legal_acceptance_before_provisioning(db):
    with RawTestClient(app, headers=DEV_AUTH_HEADERS, client=("127.0.0.1", 50000)) as raw:
        missing = raw.post("/api/auth/dev", json={"email": "missing-legal@example.com"})
    assert missing.status_code == 422
    assert missing.json()["detail"]["code"] == "VALIDATION_ERROR"

    with RawTestClient(app, headers=DEV_AUTH_HEADERS, client=("127.0.0.1", 50000)) as raw:
        unchecked = raw.post(
            "/api/auth/dev",
            json={
                "email": "unchecked-legal@example.com",
                "legal_accepted": False,
                "legal_bundle_version": CURRENT_LEGAL_BUNDLE_VERSION,
            },
        )
        stale = raw.post(
            "/api/auth/dev",
            json={
                "email": "stale-legal@example.com",
                "legal_accepted": True,
                "legal_bundle_version": "2026-07-19-v1",
            },
        )
    assert unchecked.status_code == 428
    assert unchecked.json()["detail"]["code"] == "LEGAL_ACCEPTANCE_REQUIRED"
    assert stale.status_code == 409
    assert stale.json()["detail"]["code"] == "LEGAL_BUNDLE_STALE"
    db.rollback()
    assert db.scalar(select(func.count()).select_from(User)) == 0
    assert db.scalar(select(func.count()).select_from(LegalAcceptance)) == 0
    assert db.scalar(select(func.count()).select_from(UserSession)) == 0


def test_login_rejects_coerced_non_boolean_acceptance():
    with RawTestClient(app, headers=DEV_AUTH_HEADERS, client=("127.0.0.1", 50000)) as raw:
        response = raw.post(
            "/api/auth/dev",
            json={
                "email": "non-boolean-legal@example.com",
                "legal_accepted": "true",
                "legal_bundle_version": CURRENT_LEGAL_BUNDLE_VERSION,
            },
        )
    assert response.status_code == 422
    assert response.json()["detail"]["code"] == "VALIDATION_ERROR"


@pytest.mark.parametrize("path", ["/api/auth/google", "/api/auth/microsoft"])
def test_provider_login_rejects_stale_bundle_before_identity_verification(
    client, monkeypatch, path
):
    def must_not_verify(_token: str):
        raise AssertionError("provider verifier must not run for a stale legal bundle")

    module = "app.routers.auth.verify_google_id_token"
    if path.endswith("microsoft"):
        module = "app.routers.auth.verify_microsoft_id_token"
    monkeypatch.setattr(module, must_not_verify)
    response = client.post(
        path,
        json={
            "id_token": "not-reached",
            "legal_accepted": True,
            "legal_bundle_version": "stale-v0",
        },
    )
    assert response.status_code == 409
    assert response.json()["detail"]["code"] == "LEGAL_BUNDLE_STALE"


def test_every_session_records_bounded_immutable_exact_acceptance_evidence(client, db):
    long_user_agent = "LegalBrowser/1.0 " + ("x" * 600)
    headers = {"user-agent": long_user_agent, "x-request-id": "legal-request-001"}
    first = client.post("/api/auth/dev", json=_payload(), headers=headers)
    assert first.status_code == 200, first.text
    second = client.post("/api/auth/dev", json=_payload(), headers=headers)
    assert second.status_code == 200, second.text

    db.rollback()

    rows = list(
        db.scalars(
            select(LegalAcceptance)
            .where(LegalAcceptance.user_id == first.json()["id"])
            .order_by(LegalAcceptance.id)
        )
    )
    assert len(rows) == 2
    for evidence in rows:
        assert evidence.bundle_version == CURRENT_LEGAL_BUNDLE_VERSION
        assert evidence.terms_version == "2026-07-20-v1"
        assert evidence.eula_version == "2026-07-20-v1"
        assert evidence.acceptable_use_version == "2026-07-20-v1"
        assert evidence.privacy_version == "2026-07-20-v1"
        assert (
            evidence.terms_sha256
            == "2c77250037d037141e79fd11f1a85cde1e9257d51cb325e7fdaefa6cf4f0ff2e"
        )
        assert (
            evidence.eula_sha256
            == "16bd045a449990e3f7325f0d67d81d4fee54f679ec53164835f0c19725e25638"
        )
        assert (
            evidence.acceptable_use_sha256
            == "d4391a0abe57885964606521039a4cca0151f8e11d95c628efc51b603eefdb0d"
        )
        assert (
            evidence.privacy_sha256
            == "fb96f77cc9846282c9555105994d0dc9b400c2a6eaf35e15b390b5a3c5db2d3d"
        )
        assert evidence.source_ip == "127.0.0.1"
        assert evidence.user_agent == long_user_agent[:400]
        assert evidence.request_id == "legal-request-001"
        assert evidence.retention_until - evidence.accepted_at == timedelta(days=2555)

    legal_events = list(
        db.scalars(
            select(SecurityEvent).where(
                SecurityEvent.event_type == "legal.accepted",
                SecurityEvent.actor_user_id == first.json()["id"],
            )
        )
    )
    assert len(legal_events) == 2
    assert all(event.target_id == CURRENT_LEGAL_BUNDLE_VERSION for event in legal_events)
    assert all(
        event.event_metadata["terms_sha256"]
        == "2c77250037d037141e79fd11f1a85cde1e9257d51cb325e7fdaefa6cf4f0ff2e"
        for event in legal_events
    )

    rows[0].bundle_version = "rewritten"
    with pytest.raises(TypeError, match="append-only"):
        db.commit()
    db.rollback()


def test_provider_account_gate_failure_records_no_acceptance_or_session(client, db, monkeypatch):
    identity = VerifiedIdentity(
        provider="google",
        subject="legal-disabled-subject",
        email="legal-disabled@example.com",
        display_name="Disabled legal user",
    )
    user = find_or_create_user(db, identity)
    user.is_active = False
    user.deactivated_at = datetime.now(UTC).replace(tzinfo=None)
    db.commit()
    monkeypatch.setattr("app.routers.auth.verify_google_id_token", lambda _token: identity)

    response = client.post("/api/auth/google", json={"id_token": "verified-but-disabled"})
    assert response.status_code == 403
    assert response.json()["detail"]["code"] == "ACCOUNT_DISABLED"
    db.rollback()
    assert (
        db.scalar(
            select(func.count())
            .select_from(LegalAcceptance)
            .where(LegalAcceptance.user_id == user.id)
        )
        == 0
    )
    assert (
        db.scalar(
            select(func.count()).select_from(UserSession).where(UserSession.user_id == user.id)
        )
        == 0
    )


def test_account_erasure_scrubs_acceptance_request_metadata_but_retains_version_fact(client, db):
    response = client.post(
        "/api/auth/dev",
        json=_payload("legal-erasure@example.com"),
        headers={"user-agent": "Sensitive device", "x-request-id": "sensitive-request"},
    )
    user_id = response.json()["id"]
    user = db.get(User, user_id)
    assert user is not None
    anonymize_user(db, user, policy_trigger="legal_evidence_test")
    db.commit()

    db.expire_all()
    evidence = db.scalar(select(LegalAcceptance).where(LegalAcceptance.user_id == user_id))
    assert evidence is not None
    assert evidence.bundle_version == CURRENT_LEGAL_BUNDLE_VERSION
    assert evidence.accepted_at is not None
    assert evidence.source_ip is None
    assert evidence.user_agent is None
    assert evidence.request_id is None
    assert evidence.personal_data_erased_at is not None


def test_expired_acceptance_retention_is_bounded_and_hold_aware(client, db):
    response = client.post("/api/auth/dev", json=_payload("legal-retention@example.com"))
    user_id = response.json()["id"]
    now = datetime.now(UTC).replace(tzinfo=None)
    current = db.scalar(select(LegalAcceptance).where(LegalAcceptance.user_id == user_id))
    assert current is not None
    for index in range(3):
        db.add(
            _historic_acceptance(
                user_id=user_id,
                bundle=f"historic-{index}",
                accepted_at=now - timedelta(days=3000),
                retention_until=now - timedelta(seconds=1),
            )
        )
    db.commit()

    site = db.get(SiteSettings, 1)
    assert site is not None
    site.retention_hold = True
    site.retention_hold_reference = "LEGAL-RETENTION-TEST"
    db.commit()
    held = run_retention_cycle(legal_acceptance_batch_size=2, max_batches=1)
    assert held.legal_acceptances_deleted == 0

    db.refresh(site)
    site.retention_hold = False
    site.retention_hold_reference = None
    db.commit()
    first = run_retention_cycle(legal_acceptance_batch_size=2, max_batches=1)
    second = run_retention_cycle(legal_acceptance_batch_size=2, max_batches=1)
    assert first.legal_acceptances_deleted == 2
    assert second.legal_acceptances_deleted == 1
    db.expire_all()
    remaining = list(db.scalars(select(LegalAcceptance).where(LegalAcceptance.user_id == user_id)))
    assert len(remaining) == 1
    assert remaining[0].bundle_version == CURRENT_LEGAL_BUNDLE_VERSION


def test_policy_reduction_expires_existing_cohorts_but_increase_never_extends(
    client, db, monkeypatch
):
    response = client.post("/api/auth/dev", json=_payload("legal-policy-change@example.com"))
    user_id = response.json()["id"]
    now = datetime.now(UTC).replace(tzinfo=None)
    db.add_all(
        [
            _historic_acceptance(
                user_id=user_id,
                bundle="stored-deadline-first",
                accepted_at=now - timedelta(days=10),
                retention_until=now - timedelta(seconds=1),
            ),
            _historic_acceptance(
                user_id=user_id,
                bundle="policy-reduction-first",
                accepted_at=now - timedelta(days=100),
                retention_until=now + timedelta(days=1000),
            ),
        ]
    )
    db.commit()

    monkeypatch.setattr(settings, "legal_acceptance_retention_days", 3650)
    increased = run_retention_cycle(legal_acceptance_batch_size=10, max_batches=1)
    assert increased.legal_acceptances_deleted == 1
    db.expire_all()
    assert (
        db.scalar(
            select(func.count())
            .select_from(LegalAcceptance)
            .where(LegalAcceptance.bundle_version == "stored-deadline-first")
        )
        == 0
    )
    assert (
        db.scalar(
            select(func.count())
            .select_from(LegalAcceptance)
            .where(LegalAcceptance.bundle_version == "policy-reduction-first")
        )
        == 1
    )

    db.rollback()  # end the MySQL REPEATABLE READ snapshot before the next worker transaction
    monkeypatch.setattr(settings, "legal_acceptance_retention_days", 30)
    reduced = run_retention_cycle(legal_acceptance_batch_size=10, max_batches=1)
    assert reduced.legal_acceptances_deleted == 1
    db.rollback()
    assert (
        db.scalar(
            select(func.count())
            .select_from(LegalAcceptance)
            .where(LegalAcceptance.bundle_version == "policy-reduction-first")
        )
        == 0
    )


def test_database_rejects_noncanonical_legal_digest(client, db):
    response = client.post("/api/auth/dev", json=_payload("invalid-digest@example.com"))
    user_id = response.json()["id"]
    acceptance = db.scalar(select(LegalAcceptance).where(LegalAcceptance.user_id == user_id))
    assert acceptance is not None
    table = LegalAcceptance.__table__
    with pytest.raises(DBAPIError):
        db.execute(table.update().where(table.c.id == acceptance.id).values(terms_sha256="A" * 64))
        db.commit()
    db.rollback()
