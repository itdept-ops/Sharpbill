import pytest
from fastapi import Response
from sqlalchemy import select

from app.config import settings
from app.db import SessionLocal
from app.errors import ApiError
from app.main import app
from app.models import SecurityEvent, SiteSettings, User
from app.privacy_lifecycle import anonymize_user
from app.routers.auth import update_location
from app.routers.users import update_profile
from app.schemas.auth import LocationUpdate
from app.schemas.user import ProfileUpdate
from tests.client import TestClient


def _login(client: TestClient, email: str, role: str = "user") -> dict:
    response = client.post("/api/auth/dev", json={"email": email, "role": role})
    assert response.status_code == 200, response.text
    return response.json()


def test_privacy_status_exposes_enforced_policy_and_self_service_erasure(client, db):
    user = _login(client, "privacy-self@example.com")

    initial = client.get("/api/privacy")
    assert initial.status_code == 200
    assert initial.json()["policy"] == {
        "precise_location_hours": settings.precise_location_retention_hours,
        "pending_accounts_days": settings.pending_account_retention_days,
        "sessions_after_expiry_or_revocation_days": settings.session_retention_days,
        "request_activity_days": settings.request_log_retention_days,
        "erasure_grace_days": settings.account_erasure_grace_days,
        "disabled_accounts_days": settings.disabled_account_retention_days,
        "security_events_days": settings.security_event_retention_days,
        "legal_acceptances_days": settings.legal_acceptance_retention_days,
        "generated_exports_retained": False,
    }

    requested = client.post("/api/privacy/erasure-request")
    assert requested.status_code == 200, requested.text
    assert requested.json()["erasure_requested_at"] is not None
    assert requested.json()["erasure_due_at"] is not None

    db.expire_all()
    stored = db.get(User, user["id"])
    assert stored is not None
    assert stored.erasure_requested_at is not None
    assert stored.erasure_due_at is not None
    delta = stored.erasure_due_at - stored.erasure_requested_at
    assert delta.days == settings.account_erasure_grace_days

    cancelled = client.delete("/api/privacy/erasure-request")
    assert cancelled.status_code == 200
    assert cancelled.json()["erasure_requested_at"] is None
    assert cancelled.json()["erasure_due_at"] is None

    db.rollback()
    event_types = set(
        db.scalars(
            select(SecurityEvent.event_type).where(SecurityEvent.actor_user_id == user["id"])
        )
    )
    assert {"privacy.erasure.requested", "privacy.erasure.cancelled"} <= event_types


def test_clear_saved_location_removes_precise_and_derived_values(client):
    _login(client, "privacy-location@example.com")
    assert (
        client.post(
            "/api/auth/location",
            json={"latitude": 37.7749, "longitude": -122.4194, "accuracy": 8},
        ).status_code
        == 204
    )

    response = client.delete("/api/privacy/location")
    assert response.status_code == 204
    me = client.get("/api/auth/me").json()
    assert me["last_latitude"] is None
    assert me["last_longitude"] is None
    assert me["last_location_accuracy"] is None
    assert me["last_location_at"] is None
    assert me["location"] is None
    assert me["timezone"] is None


def test_retention_hold_is_privileged_audited_and_blocks_deletion(client, db):
    admin = _login(client, "privacy-admin@example.com", "admin")
    member_client = TestClient(app)
    member = _login(member_client, "privacy-held@example.com")
    assert member_client.post("/api/privacy/erasure-request").status_code == 200

    enabled = client.put(
        "/api/admin/privacy/hold",
        json={"enabled": True, "reference": "LEGAL-2026-0042"},
    )
    assert enabled.status_code == 200, enabled.text
    assert enabled.json()["retention_hold"] is True
    assert enabled.json()["retention_hold_reference"] == "LEGAL-2026-0042"

    assert member_client.delete("/api/privacy/location").status_code == 423
    assert member_client.post("/api/privacy/erasure-request").status_code == 423
    # Cancellation preserves data, so it remains available during a hold.
    assert member_client.delete("/api/privacy/erasure-request").status_code == 200

    plain_client = TestClient(app)
    _login(plain_client, "privacy-plain@example.com")
    assert plain_client.get("/api/admin/privacy").status_code == 403
    assert (
        plain_client.put(
            "/api/admin/privacy/hold",
            json={"enabled": False},
        ).status_code
        == 403
    )

    invalid_release = client.put(
        "/api/admin/privacy/hold",
        json={"enabled": False, "reference": "must-not-be-sent"},
    )
    assert invalid_release.status_code == 422
    released = client.put("/api/admin/privacy/hold", json={"enabled": False})
    assert released.status_code == 200
    assert released.json()["retention_hold"] is False
    assert released.json()["retention_hold_reference"] is None

    db.expire_all()
    site = db.get(SiteSettings, 1)
    assert site is not None and not site.retention_hold
    hold_events = list(
        db.scalars(
            select(SecurityEvent)
            .where(
                SecurityEvent.event_type == "privacy.retention_hold.changed",
                SecurityEvent.actor_user_id == admin["id"],
            )
            .order_by(SecurityEvent.id)
        )
    )
    assert len(hold_events) == 2
    assert hold_events[0].event_metadata["after_reference"] == "LEGAL-2026-0042"
    assert hold_events[1].event_metadata["before_reference"] == "LEGAL-2026-0042"
    assert hold_events[1].event_metadata["after_reference"] is None

    # The held member remains an ordinary, active account after cancelling the request.
    held_user = db.get(User, member["id"])
    assert held_user is not None and held_user.erasure_due_at is None


def test_admin_can_schedule_and_cancel_non_admin_but_not_admin_erasure(client, db):
    admin = _login(client, "privacy-control@example.com", "admin")
    member_client = TestClient(app)
    member = _login(member_client, "privacy-target@example.com")

    scheduled = client.post(f"/api/admin/privacy/users/{member['id']}/erasure-request")
    assert scheduled.status_code == 200, scheduled.text
    assert scheduled.json()["erasure_due_at"] is not None

    cancelled = client.delete(f"/api/admin/privacy/users/{member['id']}/erasure-request")
    assert cancelled.status_code == 200
    assert cancelled.json()["erasure_due_at"] is None

    self_admin = client.post(f"/api/admin/privacy/users/{admin['id']}/erasure-request")
    assert self_admin.status_code == 400
    personal_admin = client.post("/api/privacy/erasure-request")
    assert personal_admin.status_code == 409

    db.expire_all()
    member_row = db.get(User, member["id"])
    assert member_row is not None and member_row.erasure_requested_at is None


def test_retention_hold_reference_is_a_non_freeform_external_key(client):
    _login(client, "privacy-ref-admin@example.com", "admin")
    assert client.put("/api/admin/privacy/hold", json={"enabled": True}).status_code == 422
    assert (
        client.put(
            "/api/admin/privacy/hold",
            json={"enabled": True, "reference": "contains spaces and personal details"},
        ).status_code
        == 422
    )


def test_stale_personal_mutations_cannot_reintroduce_pii_after_erasure(client):
    user = _login(client, "privacy-mutation-race@example.com")
    location_db = SessionLocal()
    profile_db = SessionLocal()
    try:
        location_user = location_db.get(User, user["id"])
        profile_user = profile_db.get(User, user["id"])
        assert location_user is not None and profile_user is not None

        with SessionLocal() as eraser:
            target = eraser.get(User, user["id"])
            assert target is not None
            anonymize_user(eraser, target, policy_trigger="personal_mutation_race_test")
            eraser.commit()

        with pytest.raises(ApiError) as location_error:
            update_location(
                LocationUpdate(latitude=47.6062, longitude=-122.3321, accuracy=5),
                Response(),
                db=location_db,
                user=location_user,
            )
        assert location_error.value.code == "INVALID_SESSION"

        with pytest.raises(ApiError) as profile_error:
            update_profile(
                user["id"],
                ProfileUpdate(display_name="Restored personal data", location="Seattle"),
                db=profile_db,
                current=profile_user,
            )
        assert profile_error.value.code == "ACCOUNT_ERASED"

        with SessionLocal() as verifier:
            erased = verifier.get(User, user["id"])
            assert erased is not None and erased.erased_at is not None
            assert erased.display_name is None
            assert erased.location is None
            assert erased.timezone is None
            assert erased.last_latitude is None
            assert erased.last_longitude is None
            assert erased.last_location_accuracy is None
            assert erased.last_location_at is None
    finally:
        location_db.close()
        profile_db.close()
