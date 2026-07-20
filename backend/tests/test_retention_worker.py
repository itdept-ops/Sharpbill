"""Scheduled retention runs independently of log traffic and user sign-in."""

import threading
import time
from datetime import UTC, datetime, timedelta

import pytest
from sqlalchemy import func, select

from app.config import settings
from app.models import (
    LoginNonce,
    Permission,
    RequestLog,
    SecurityEvent,
    SecurityEventDelivery,
    SiteSettings,
    User,
    UserIdentity,
    UserSession,
)
from app.privacy_lifecycle import (
    AccountErasureError,
    clear_user_location,
    request_account_erasure,
)
from app.retention import RetentionWorker, run_retention_cycle


def test_retention_cycle_caps_each_table_and_makes_monotonic_progress(client, db):
    user = client.post(
        "/api/auth/dev", json={"email": "scheduled-retention@example.com", "role": "user"}
    ).json()
    now = datetime.now(UTC).replace(tzinfo=None)
    db.add_all(
        [
            RequestLog(
                method="GET",
                path=f"/api/scheduled-old-{number}",
                user_id=user["id"],
                ip="127.0.0.1",
                status_code=200,
                created_at=now - timedelta(days=settings.request_log_retention_days + 1),
            )
            for number in range(3)
        ]
        + [
            UserSession(
                user_id=user["id"],
                jti=f"scheduled-stale-{number}",
                expires_at=now - timedelta(days=settings.session_retention_days + 1),
            )
            for number in range(3)
        ]
    )
    db.commit()

    first = run_retention_cycle(
        request_log_batch_size=2,
        session_batch_size=2,
        max_batches=1,
    )
    assert first.request_logs_deleted == 2
    assert first.sessions_deleted == 2
    assert first.request_log_batches == 1
    assert first.session_batches == 1

    db.expire_all()
    old_log_count = db.scalar(
        select(func.count())
        .select_from(RequestLog)
        .where(RequestLog.path.like("/api/scheduled-old-%"))
    )
    stale_session_count = db.scalar(
        select(func.count())
        .select_from(UserSession)
        .where(UserSession.jti.like("scheduled-stale-%"))
    )
    assert old_log_count == 1
    assert stale_session_count == 1

    second = run_retention_cycle(
        request_log_batch_size=2,
        session_batch_size=2,
        max_batches=1,
    )
    assert second.request_logs_deleted == 1
    assert second.sessions_deleted == 1


def _expired_security_event(db, number: int, now: datetime) -> SecurityEvent:
    event = SecurityEvent(
        event_type="test.retention",
        outcome="success",
        severity="info",
        event_metadata={"number": number},
        retention_until=now - timedelta(seconds=1),
    )
    db.add(event)
    db.flush()
    db.add(SecurityEventDelivery(event_id=event.id))
    return event


def test_cycle_prunes_expired_nonces_and_pending_security_events_in_bounded_batches(db):
    now = datetime.now(UTC).replace(tzinfo=None)
    db.add_all(
        LoginNonce(
            nonce=f"scheduled-expired-{number}",
            expires_at=now - timedelta(seconds=1),
        )
        for number in range(3)
    )
    events = [_expired_security_event(db, number, now) for number in range(3)]
    event_ids = [event.id for event in events]
    db.commit()

    first = run_retention_cycle(
        nonce_batch_size=2,
        security_event_batch_size=2,
        max_batches=1,
    )
    assert first.nonces_deleted == 2
    assert first.security_events_deleted == 2
    assert first.nonce_batches == first.security_event_batches == 1

    db.expire_all()
    assert db.scalar(select(func.count()).select_from(LoginNonce)) == 1
    assert (
        db.scalar(
            select(func.count()).select_from(SecurityEvent).where(SecurityEvent.id.in_(event_ids))
        )
        == 1
    )
    # Delivery state is removed by the event FK's ON DELETE CASCADE, even while still pending.
    assert (
        db.scalar(
            select(func.count())
            .select_from(SecurityEventDelivery)
            .where(SecurityEventDelivery.event_id.in_(event_ids))
        )
        == 1
    )

    second = run_retention_cycle(
        nonce_batch_size=2,
        security_event_batch_size=2,
        max_batches=1,
    )
    assert second.nonces_deleted == 1
    assert second.security_events_deleted == 1


def test_cycle_anonymizes_due_account_but_retains_identity_suppression_marker(client, db):
    now = datetime.now(UTC).replace(tzinfo=None)
    payload = client.post(
        "/api/auth/dev", json={"email": "erase-due@example.com", "role": "user"}
    ).json()
    user = db.get(User, payload["id"])
    assert user is not None
    direct_permission = db.scalar(select(Permission).where(Permission.key == "logs.view"))
    assert direct_permission is not None
    user.granted_permissions = [direct_permission]
    user.display_name = "Erase Me"
    user.title = "Sensitive title"
    user.department = "Sensitive department"
    user.phone = "+1-555-0100"
    user.location = "Sensitive place"
    user.timezone = "America/Los_Angeles"
    user.bio = "Sensitive biography"
    user.last_latitude = 47.6
    user.last_longitude = -122.3
    user.last_location_accuracy = 20
    user.last_location_at = now
    user.location_retention_until = now + timedelta(hours=settings.precise_location_retention_hours)
    user.erasure_requested_at = now - timedelta(days=31)
    user.erasure_due_at = now - timedelta(seconds=1)
    identity = db.scalar(select(UserIdentity).where(UserIdentity.user_id == user.id))
    assert identity is not None
    identity_marker = (
        identity.provider,
        identity.provider_namespace,
        identity.provider_subject,
        identity.provider_tenant_id,
        identity.provider_hosted_domain,
    )
    db.commit()

    result = run_retention_cycle(account_batch_size=1, max_batches=1)
    assert result.accounts_anonymized == 1

    db.expire_all()
    erased = db.get(User, user.id)
    assert erased is not None
    assert erased.email == f"erased-{user.id}@privacy.invalid"
    assert erased.erased_at is not None
    assert erased.deactivated_at is not None
    assert erased.erasure_requested_at is None
    assert erased.erasure_due_at is None
    assert erased.role_name == "user"
    assert not erased.is_active and not erased.is_approved
    assert erased.granted_permissions == []
    assert all(
        value is None
        for value in (
            erased.display_name,
            erased.title,
            erased.department,
            erased.phone,
            erased.location,
            erased.timezone,
            erased.bio,
            erased.accent_color,
            erased.ui_prefs,
            erased.last_login_at,
            erased.last_seen_at,
            erased.last_latitude,
            erased.last_longitude,
            erased.last_location_accuracy,
            erased.last_location_at,
            erased.location_retention_until,
        )
    )
    retained_identity = db.scalar(select(UserIdentity).where(UserIdentity.user_id == erased.id))
    assert retained_identity is not None
    assert (
        retained_identity.provider,
        retained_identity.provider_namespace,
        retained_identity.provider_subject,
        retained_identity.provider_tenant_id,
        retained_identity.provider_hosted_domain,
    ) == identity_marker
    assert (
        db.scalar(
            select(func.count()).select_from(UserSession).where(UserSession.user_id == erased.id)
        )
        == 0
    )
    event = db.scalar(
        select(SecurityEvent).where(
            SecurityEvent.event_type == "privacy.account.erased",
            SecurityEvent.target_id == str(erased.id),
        )
    )
    assert event is not None
    assert event.event_metadata == {"policy_trigger": "requested_erasure_due"}

    reprovision = client.post(
        "/api/auth/dev", json={"email": "erase-due@example.com", "role": "admin"}
    )
    assert reprovision.status_code == 403
    assert reprovision.json()["detail"]["code"] == "ACCOUNT_ERASED"
    db.rollback()
    assert (
        db.scalar(
            select(func.count()).select_from(UserSession).where(UserSession.user_id == erased.id)
        )
        == 0
    )

    # The worker is safely idempotent: an erased tombstone is never selected again and its
    # durable completion event is not duplicated.
    repeated = run_retention_cycle(account_batch_size=1, max_batches=1)
    assert repeated.accounts_anonymized == 0
    assert (
        db.scalar(
            select(func.count())
            .select_from(SecurityEvent)
            .where(
                SecurityEvent.event_type == "privacy.account.erased",
                SecurityEvent.target_id == str(erased.id),
            )
        )
        == 1
    )


def test_cycle_bounds_gps_pending_and_disabled_lifecycle_batches(client, db):
    now = datetime.now(UTC).replace(tzinfo=None)

    def login_id(email: str) -> int:
        payload = client.post("/api/auth/dev", json={"email": email, "role": "user"}).json()
        return int(payload["id"])

    user_ids = [
        login_id("stale-location@example.com"),
        login_id("fresh-location@example.com"),
        login_id("expired-pending@example.com"),
        login_id("expired-disabled@example.com"),
    ]
    db.rollback()  # leave any pre-login REPEATABLE READ snapshot before loading all four rows
    users = [db.get(User, user_id) for user_id in user_ids]
    assert all(user is not None for user in users)
    stale_location, fresh_location, pending, disabled = users
    assert stale_location is not None
    assert fresh_location is not None
    assert pending is not None
    assert disabled is not None

    stale_location.last_latitude = 47.6
    stale_location.last_longitude = -122.3
    stale_location.last_location_accuracy = 10
    stale_location.last_location_at = now - timedelta(
        hours=settings.precise_location_retention_hours + 1
    )
    stale_location.location_retention_until = stale_location.last_location_at + timedelta(
        hours=settings.precise_location_retention_hours
    )
    fresh_location.last_latitude = 40.7
    fresh_location.last_longitude = -74.0
    fresh_location.last_location_accuracy = 10
    fresh_location.last_location_at = now - timedelta(
        hours=settings.precise_location_retention_hours - 1
    )
    fresh_location.location_retention_until = fresh_location.last_location_at + timedelta(
        hours=settings.precise_location_retention_hours
    )

    pending.is_approved = False
    pending.created_at = now - timedelta(days=settings.pending_account_retention_days + 1)
    disabled.is_active = False
    disabled.deactivated_at = now - timedelta(days=settings.disabled_account_retention_days + 1)
    db.commit()

    first = run_retention_cycle(
        precise_location_batch_size=1,
        account_batch_size=1,
        max_batches=1,
    )
    assert first.precise_locations_cleared == 1
    assert first.accounts_anonymized == 1
    db.expire_all()
    assert db.get(User, stale_location.id).last_latitude is None
    assert db.get(User, fresh_location.id).last_latitude == 40.7
    assert db.get(User, pending.id).erased_at is not None
    assert db.get(User, disabled.id).erased_at is None

    # End the read snapshot opened by those assertions before the independent worker transaction.
    db.rollback()
    second = run_retention_cycle(account_batch_size=1, max_batches=1)
    assert second.accounts_anonymized == 1
    db.expire_all()
    assert db.get(User, disabled.id).erased_at is not None
    triggers = {
        event.event_metadata["policy_trigger"]
        for event in db.scalars(
            select(SecurityEvent).where(
                SecurityEvent.event_type == "privacy.account.erased",
                SecurityEvent.target_id.in_((str(pending.id), str(disabled.id))),
            )
        )
    }
    assert triggers == {
        "pending_account_expired",
        "disabled_account_expired",
    }


def test_location_clear_helper_removes_coarse_and_precise_profile(client, db):
    payload = client.post(
        "/api/auth/dev", json={"email": "clear-location@example.com", "role": "user"}
    ).json()
    user = db.get(User, payload["id"])
    assert user is not None
    user.location = "Seattle"
    user.timezone = "America/Los_Angeles"
    user.last_latitude = 47.6
    user.last_longitude = -122.3
    user.last_location_accuracy = 15
    user.last_location_at = datetime.now(UTC).replace(tzinfo=None)
    user.location_retention_until = user.last_location_at + timedelta(
        hours=settings.precise_location_retention_hours
    )

    assert clear_user_location(user)
    assert all(
        value is None
        for value in (
            user.location,
            user.timezone,
            user.last_latitude,
            user.last_longitude,
            user.last_location_accuracy,
            user.last_location_at,
            user.location_retention_until,
        )
    )
    assert not clear_user_location(user)


def test_location_policy_reduction_applies_but_increase_never_extends_capture_deadline(
    client, db, monkeypatch
):
    now = datetime.now(UTC).replace(tzinfo=None)

    def login_user(email: str) -> User:
        payload = client.post("/api/auth/dev", json={"email": email, "role": "user"}).json()
        db.rollback()
        user = db.get(User, payload["id"])
        assert user is not None
        return user

    stored_deadline = login_user("location-stored-deadline@example.com")
    policy_reduction = login_user("location-policy-reduction@example.com")
    stored_deadline.last_latitude = 1
    stored_deadline.last_longitude = 2
    stored_deadline.last_location_at = now - timedelta(hours=1)
    stored_deadline.location_retention_until = now - timedelta(seconds=1)
    policy_reduction.last_latitude = 3
    policy_reduction.last_longitude = 4
    policy_reduction.last_location_at = now - timedelta(hours=10)
    policy_reduction.location_retention_until = now + timedelta(hours=100)
    db.commit()

    monkeypatch.setattr(settings, "precise_location_retention_hours", 20)
    increased = run_retention_cycle(precise_location_batch_size=10, max_batches=1)
    assert increased.precise_locations_cleared == 1
    db.expire_all()
    assert db.get(User, stored_deadline.id).last_latitude is None
    assert db.get(User, policy_reduction.id).last_latitude == 3

    # End the assertion read snapshot before the independent worker's next transaction.
    db.rollback()
    monkeypatch.setattr(settings, "precise_location_retention_hours", 6)
    reduced = run_retention_cycle(precise_location_batch_size=10, max_batches=1)
    assert reduced.precise_locations_cleared == 1
    db.expire_all()
    reduced_user = db.get(User, policy_reduction.id)
    assert reduced_user is not None
    assert reduced_user.last_latitude is None
    assert reduced_user.location_retention_until is None


def test_admin_erasure_is_refused_and_never_selected(client, db):
    now = datetime.now(UTC).replace(tzinfo=None)
    payload = client.post(
        "/api/auth/dev", json={"email": "retention-admin@example.com", "role": "admin"}
    ).json()
    admin = db.get(User, payload["id"])
    assert admin is not None
    with pytest.raises(AccountErasureError, match="administrator"):
        request_account_erasure(admin, now=now)

    # Defense in depth: even a directly corrupted due timestamp cannot make the worker erase admin.
    admin.erasure_requested_at = now - timedelta(days=31)
    admin.erasure_due_at = now - timedelta(seconds=1)
    db.commit()
    result = run_retention_cycle(account_batch_size=1, max_batches=1)
    assert result.accounts_anonymized == 0
    db.expire_all()
    assert db.get(User, admin.id).erased_at is None


def test_global_hold_preserves_governed_data_but_never_expired_nonces(client, db):
    now = datetime.now(UTC).replace(tzinfo=None)
    payload = client.post(
        "/api/auth/dev", json={"email": "held-retention@example.com", "role": "user"}
    ).json()
    user = db.get(User, payload["id"])
    site = db.get(SiteSettings, 1)
    assert user is not None and site is not None
    user.last_latitude = 40.0
    user.last_longitude = -75.0
    user.last_location_accuracy = 25
    user.last_location_at = now - timedelta(hours=settings.precise_location_retention_hours + 1)
    user.location_retention_until = user.last_location_at + timedelta(
        hours=settings.precise_location_retention_hours
    )
    user.erasure_requested_at = now - timedelta(days=31)
    user.erasure_due_at = now - timedelta(seconds=1)
    old_log = RequestLog(
        method="GET",
        path="/api/held-old",
        user_id=user.id,
        ip="127.0.0.1",
        status_code=200,
        created_at=now - timedelta(days=settings.request_log_retention_days + 1),
    )
    stale_session = UserSession(
        user_id=user.id,
        jti="held-stale-session",
        expires_at=now - timedelta(days=settings.session_retention_days + 1),
    )
    nonce = LoginNonce(nonce="held-expired-nonce", expires_at=now - timedelta(seconds=1))
    nonce_key = nonce.nonce
    db.add_all([old_log, stale_session, nonce])
    held_event = _expired_security_event(db, 99, now)
    site.retention_hold = True
    site.retention_hold_reference = "CASE-RETENTION-001"
    db.commit()

    try:
        result = run_retention_cycle(
            nonce_batch_size=10,
            request_log_batch_size=10,
            session_batch_size=10,
            precise_location_batch_size=10,
            account_batch_size=10,
            security_event_batch_size=10,
            max_batches=1,
        )
        assert result.nonces_deleted == 1
        assert result.request_logs_deleted == 0
        assert result.sessions_deleted == 0
        assert result.precise_locations_cleared == 0
        assert result.accounts_anonymized == 0
        assert result.security_events_deleted == 0

        db.expire_all()
        held_user = db.get(User, user.id)
        assert held_user is not None and held_user.erased_at is None
        assert held_user.last_latitude == 40.0
        assert db.get(RequestLog, old_log.id) is not None
        assert db.get(UserSession, stale_session.id) is not None
        assert db.get(SecurityEvent, held_event.id) is not None
        assert db.get(LoginNonce, nonce_key) is None
    finally:
        db.rollback()
        site = db.get(SiteSettings, 1)
        assert site is not None
        site.retention_hold = False
        site.retention_hold_reference = None
        db.commit()


def test_retention_worker_delays_first_run_and_stops_promptly():
    calls: list[object] = []
    called = threading.Event()

    def cycle() -> None:
        calls.append(object())
        called.set()

    worker = RetentionWorker(
        interval_seconds=0.05,
        shutdown_timeout_seconds=1,
        run_cycle=cycle,
    )
    worker.start()
    time.sleep(0.01)
    assert not called.is_set(), "the first cycle must wait for a complete interval"
    assert called.wait(1)

    assert worker.shutdown()
    completed_calls = len(calls)
    time.sleep(0.06)
    assert len(calls) == completed_calls


def test_retention_worker_reports_bounded_shutdown_when_cycle_is_stuck():
    entered = threading.Event()
    release = threading.Event()

    def stuck_cycle() -> None:
        entered.set()
        release.wait(2)

    worker = RetentionWorker(
        interval_seconds=0.01,
        shutdown_timeout_seconds=0.02,
        run_cycle=stuck_cycle,
    )
    try:
        worker.start()
        assert entered.wait(1)
        started = time.monotonic()
        assert not worker.shutdown()
        assert time.monotonic() - started < 0.5
    finally:
        release.set()
        assert worker.shutdown()
