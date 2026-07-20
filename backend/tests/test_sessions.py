"""Per-device session tracking and revocation."""

from datetime import UTC, datetime, timedelta

import pytest
from sqlalchemy import func, select
from starlette.requests import Request

from app.auth.deps import _touch
from app.auth.sessions import (
    SessionPrincipalUnavailable,
    prune_stale_sessions,
    revoke_session,
    start_session,
)
from app.config import settings
from app.db import SessionLocal
from app.main import app
from app.models import User, UserSession
from app.privacy_lifecycle import anonymize_user
from tests.client import TestClient


def _login(client, email, role="user"):
    return client.post("/api/auth/dev", json={"email": email, "role": role}).json()


def test_lists_own_session_and_marks_current(client):
    _login(client, "sess@example.com")
    sessions = client.get("/api/auth/sessions").json()
    assert len(sessions) == 1
    assert sessions[0]["current"] is True


def test_revoking_one_device_leaves_the_others(client):
    _login(client, "multi@example.com")  # device 1
    device2 = TestClient(app)
    device2.post("/api/auth/dev", json={"email": "multi@example.com", "role": "user"})  # device 2

    sessions = client.get("/api/auth/sessions").json()
    assert len(sessions) == 2
    other = next(s for s in sessions if not s["current"])  # device 2, from device 1's view

    assert client.delete(f"/api/auth/sessions/{other['id']}").status_code == 204
    assert device2.get("/api/auth/me").status_code == 401  # device 2 signed out
    assert client.get("/api/auth/me").status_code == 200  # device 1 still valid


def test_cannot_revoke_another_users_session(client):
    victim = TestClient(app)
    victim.post("/api/auth/dev", json={"email": "victim-sess@example.com", "role": "user"})
    vsession = victim.get("/api/auth/sessions").json()[0]

    _login(client, "attacker@example.com")
    assert client.delete(f"/api/auth/sessions/{vsession['id']}").status_code == 404
    assert victim.get("/api/auth/me").status_code == 200  # untouched


def test_admin_lists_and_revokes_a_users_session(client):
    target = TestClient(app)
    tu = target.post(
        "/api/auth/dev", json={"email": "target-sess@example.com", "role": "user"}
    ).json()

    _login(client, "admin@example.com", role="admin")
    sessions = client.get(f"/api/users/{tu['id']}/sessions").json()
    assert len(sessions) == 1

    assert client.delete(f"/api/users/{tu['id']}/sessions/{sessions[0]['id']}").status_code == 204
    assert target.get("/api/auth/me").status_code == 401  # signed out on the next request


def test_logout_only_signs_out_the_current_device(client):
    _login(client, "lo@example.com")  # device 1
    device2 = TestClient(app)
    device2.post("/api/auth/dev", json={"email": "lo@example.com", "role": "user"})  # device 2

    assert client.post("/api/auth/logout").status_code == 204
    assert device2.get("/api/auth/me").status_code == 200  # other device stays signed in


def test_session_device_details_masked_for_read_only_viewer(client):
    """IP and user-agent are shown to managers/self but masked for a directory-only viewer."""
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "iptarget@example.com", "role": "user"}).json()

    _login(client, "admin@example.com", role="admin")
    client.post("/api/roles", json={"name": "ReadOnly", "permission_keys": ["users.read"]})
    viewer = TestClient(app)
    viewer.post("/api/auth/dev", json={"email": "ro-view@example.com", "role": "ReadOnly"})

    own_session = target.get("/api/auth/sessions").json()[0]
    assert own_session["ip"] is not None
    assert own_session["user_agent"] is not None

    # A manager (admin holds users.manage) sees the source device details.
    managed_session = client.get(f"/api/users/{tu['id']}/sessions").json()[0]
    assert managed_session["ip"] is not None
    assert managed_session["user_agent"] is not None

    # A users.read-only viewer gets both identifying values masked.
    masked_session = viewer.get(f"/api/users/{tu['id']}/sessions").json()[0]
    assert masked_session["ip"] is None
    assert masked_session["user_agent"] is None


def test_deactivation_revokes_session_rows(client):
    """FND-030: deactivating a user marks their session rows revoked (no phantom devices)."""
    target = TestClient(app)
    tu = target.post("/api/auth/dev", json={"email": "deac@example.com", "role": "user"}).json()

    _login(client, "admin@example.com", role="admin")
    assert len(client.get(f"/api/users/{tu['id']}/sessions").json()) == 1

    client.patch(f"/api/users/{tu['id']}/status", json={"is_active": False})
    # The active-session list is now empty — rows are revoked, not merely epoch-blocked.
    assert client.get(f"/api/users/{tu['id']}/sessions").json() == []


def test_kick_revokes_all_of_a_users_sessions(client):
    target = TestClient(app)
    tu = target.post(
        "/api/auth/dev", json={"email": "kick-sess@example.com", "role": "user"}
    ).json()
    device2 = TestClient(app)
    device2.post("/api/auth/dev", json={"email": "kick-sess@example.com", "role": "user"})

    _login(client, "admin@example.com", role="admin")
    assert client.post(f"/api/users/{tu['id']}/kick").status_code == 200

    assert target.get("/api/auth/me").status_code == 401
    assert device2.get("/api/auth/me").status_code == 401  # every device


def test_new_login_enforces_per_user_concurrent_session_cap(client, db, monkeypatch):
    monkeypatch.setattr(settings, "max_active_sessions_per_user", 2)
    first = client
    user = _login(first, "session-cap@example.com")
    second = TestClient(app)
    _login(second, "session-cap@example.com")
    third = TestClient(app)
    _login(third, "session-cap@example.com")

    now = datetime.now(UTC).replace(tzinfo=None)
    active_count = db.scalar(
        select(func.count())
        .select_from(UserSession)
        .where(
            UserSession.user_id == user["id"],
            UserSession.revoked_at.is_(None),
            UserSession.expires_at > now,
        )
    )
    assert active_count == 2
    assert first.get("/api/auth/me").status_code == 401
    assert second.get("/api/auth/me").status_code == 200
    assert third.get("/api/auth/me").status_code == 200


def test_stale_session_cleanup_is_bounded_and_keeps_recent_revocations(client, db):
    user = _login(client, "session-retention@example.com")
    now = datetime.now(UTC).replace(tzinfo=None)
    recently_expired = UserSession(
        user_id=user["id"],
        jti="cleanup-recently-expired",
        expires_at=now - timedelta(seconds=1),
    )
    stale_expired = UserSession(
        user_id=user["id"],
        jti="cleanup-stale-expired",
        expires_at=now - timedelta(days=settings.session_retention_days + 1),
    )
    old_revoked = UserSession(
        user_id=user["id"],
        jti="cleanup-old-revoked",
        expires_at=now + timedelta(days=1),
        revoked_at=now - timedelta(days=settings.session_retention_days + 1),
    )
    recent_revoked = UserSession(
        user_id=user["id"],
        jti="cleanup-recent-revoked",
        expires_at=now + timedelta(days=1),
        revoked_at=now,
    )
    db.add_all([recently_expired, stale_expired, old_revoked, recent_revoked])
    db.commit()

    assert prune_stale_sessions(db, now=now, limit=2) == 2
    remaining = set(db.scalars(select(UserSession.jti)))
    assert "cleanup-recently-expired" in remaining
    assert "cleanup-stale-expired" not in remaining
    assert "cleanup-old-revoked" not in remaining
    assert "cleanup-recent-revoked" in remaining


def test_session_issuance_rechecks_lifecycle_after_a_stale_login_read(client):
    user = _login(client, "session-erasure-race@example.com")
    stale_session = SessionLocal()
    try:
        stale_user = stale_session.get(User, user["id"])
        assert stale_user is not None and stale_user.is_active

        with SessionLocal() as eraser:
            target = eraser.get(User, user["id"])
            assert target is not None
            anonymize_user(eraser, target, policy_trigger="race_test")
            eraser.commit()

        request = Request(
            {
                "type": "http",
                "method": "POST",
                "path": "/api/auth/dev",
                "headers": [],
                "client": ("127.0.0.1", 12345),
            }
        )
        with pytest.raises(SessionPrincipalUnavailable) as caught:
            start_session(stale_session, user["id"], request)
        assert caught.value.code == "ACCOUNT_ERASED"
        stale_session.rollback()

        assert (
            stale_session.scalar(
                select(func.count())
                .select_from(UserSession)
                .where(UserSession.user_id == user["id"])
            )
            == 0
        )
    finally:
        stale_session.close()


def test_presence_touch_and_revoke_fail_closed_after_retention_deletes_session(client):
    user = _login(client, "stale-session-erasure@example.com")
    touch_db = SessionLocal()
    revoke_db = SessionLocal()
    try:
        touch_user = touch_db.get(User, user["id"])
        touch_session = touch_db.scalar(
            select(UserSession).where(UserSession.user_id == user["id"])
        )
        stale_revoke_session = revoke_db.scalar(
            select(UserSession).where(UserSession.user_id == user["id"])
        )
        assert touch_user is not None
        assert touch_session is not None
        assert stale_revoke_session is not None

        with SessionLocal() as eraser:
            target = eraser.get(User, user["id"])
            assert target is not None
            anonymize_user(eraser, target, policy_trigger="session_mutation_race_test")
            eraser.commit()

        # Both paths started with live ORM objects. Neither may restore presence metadata or
        # surface SQLAlchemy's StaleDataError/ObjectDeletedError after retention wins the race.
        assert _touch(touch_db, touch_user, touch_session) is None
        assert revoke_session(stale_revoke_session, revoke_db) is False

        with SessionLocal() as verifier:
            erased = verifier.get(User, user["id"])
            assert erased is not None and erased.erased_at is not None
            assert erased.last_seen_at is None
            assert (
                verifier.scalar(
                    select(func.count())
                    .select_from(UserSession)
                    .where(UserSession.user_id == user["id"])
                )
                == 0
            )
    finally:
        touch_db.close()
        revoke_db.close()
