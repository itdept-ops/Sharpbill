import json
from datetime import UTC, datetime, timedelta

import pytest
from sqlalchemy import inspect, select, text

from alembic import command
from alembic.config import Config
from app.auth import ProviderTokenError
from app.db import SessionLocal, engine
from app.models import Permission, SecurityEvent, SecurityEventDelivery
from app.security_events import (
    add_security_event,
    claim_delivery_batch,
    mark_delivery_failed,
    mark_delivery_succeeded,
    sanitize_metadata,
)
from tests.client import TestClient


def test_security_event_schema_separates_immutable_fact_from_delivery_state(db):
    inspector = inspect(db.bind)
    assert {"security_events", "security_event_deliveries"} <= set(inspector.get_table_names())
    event_indexes = {index["name"] for index in inspector.get_indexes("security_events")}
    assert {
        "ix_security_events_occurred_id",
        "ix_security_events_type_id",
        "ix_security_events_actor_id",
        "ix_security_events_request_id",
        "ix_security_events_retention_until",
    } <= event_indexes
    delivery_indexes = {
        index["name"] for index in inspector.get_indexes("security_event_deliveries")
    }
    assert {
        "ix_security_event_deliveries_dispatch",
        "ix_security_event_deliveries_lease",
    } <= delivery_indexes
    request_log_indexes = {index["name"] for index in inspector.get_indexes("request_logs")}
    assert "ix_request_logs_user_id_id" in request_log_indexes
    assert "ix_request_logs_user_id" not in request_log_indexes


@pytest.mark.parametrize(
    "metadata",
    [
        {"id_token": "opaque"},
        {"provider_subject": "opaque"},
        {"nested": {"authorization": "Bearer opaque"}},
        {"password": "opaque"},
    ],
)
def test_security_event_metadata_rejects_secret_and_subject_fields(metadata):
    with pytest.raises(ValueError, match="forbidden security-event metadata key"):
        sanitize_metadata(metadata)


def test_event_fact_is_orm_append_only_and_delivery_lifecycle_is_separate(db):
    event = add_security_event(
        db,
        event_type="test.dispatch",
        outcome="success",
        target_type="test_record",
        target_id=42,
        metadata={"safe": True},
    )
    db.commit()
    original = (event.event_type, event.outcome, event.event_metadata.copy(), event.occurred_at)

    event.outcome = "failure"
    with pytest.raises(TypeError, match="append-only"):
        db.commit()
    db.rollback()

    lease_time = datetime.now(UTC).replace(tzinfo=None) + timedelta(seconds=1)
    batch = claim_delivery_batch(db, worker_id="siem-worker-a", now=lease_time)
    assert [item.event_id for item in batch] == [event.id]
    assert batch[0].metadata == {"safe": True}
    assert mark_delivery_failed(
        db,
        event_id=event.id,
        worker_id="siem-worker-a",
        error="temporary sink outage\nwith detail",
        now=lease_time,
    )
    failed_delivery = db.get(SecurityEventDelivery, event.id)
    assert failed_delivery.last_error.startswith("sink_delivery_failed:")
    assert "outage" not in failed_delivery.last_error
    retry_at = failed_delivery.next_attempt_at
    batch = claim_delivery_batch(
        db,
        worker_id="siem-worker-b",
        now=retry_at + timedelta(seconds=1),
    )
    assert [item.event_id for item in batch] == [event.id]
    assert mark_delivery_succeeded(
        db,
        event_id=event.id,
        worker_id="siem-worker-b",
        now=retry_at + timedelta(seconds=2),
    )

    db.refresh(event)
    assert (event.event_type, event.outcome, event.event_metadata, event.occurred_at) == original
    delivery = db.get(SecurityEventDelivery, event.id)
    assert delivery.status == "delivered"
    assert delivery.attempts == 2
    assert delivery.delivered_at is not None


def test_staged_event_rolls_back_with_failed_business_transaction(db):
    add_security_event(
        db,
        event_type="test.transaction",
        outcome="success",
        metadata={"business_change": "staged"},
    )
    db.rollback()

    assert (
        db.scalar(select(SecurityEvent.id).where(SecurityEvent.event_type == "test.transaction"))
        is None
    )


def test_0014_downgrade_refuses_to_destroy_retained_audit_evidence():
    engine.dispose()
    try:
        command.downgrade(Config("alembic.ini"), "0014")
        with SessionLocal() as db:
            add_security_event(
                db,
                event_type="test.retained",
                outcome="success",
                metadata={"retained": True},
            )
            db.commit()

        with pytest.raises(RuntimeError, match="contains retained audit evidence"):
            command.downgrade(Config("alembic.ini"), "0013")

        # The 0014 refusal precedes every DDL statement and leaves its revision recorded.
        assert {"security_events", "security_event_deliveries"} <= set(
            inspect(engine).get_table_names()
        )
        with engine.connect() as connection:
            assert connection.scalar(text("SELECT version_num FROM alembic_version")) == "0014"
            assert connection.scalar(text("SELECT COUNT(*) FROM security_events")) == 1
    finally:
        command.upgrade(Config("alembic.ini"), "head")
        engine.dispose()


def test_admin_mutations_create_transactional_events_without_identity_subjects(client, db):
    admin = client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    target_client = TestClient(client.app)
    target = target_client.post(
        "/api/auth/dev", json={"email": "target@example.com", "role": "user"}
    )
    assert admin.status_code == target.status_code == 200

    permission = client.post(
        "/api/permissions",
        json={"key": "cases.review", "description": "Review cases"},
        headers={"X-Request-ID": "audit-permission-create"},
    )
    role = client.post(
        "/api/roles",
        json={"name": "Reviewer", "permission_keys": ["cases.review"]},
    )
    assert permission.status_code == role.status_code == 201
    assigned = client.patch(
        f"/api/users/{target.json()['id']}/role",
        json={
            "role_id": role.json()["id"],
            "expected_version": target.json()["access_version"],
        },
    )
    assert assigned.status_code == 200
    assert (
        client.put(
            f"/api/users/{target.json()['id']}/permissions",
            json={
                "permission_keys": ["cases.review"],
                "expected_version": assigned.json()["access_version"],
            },
        ).status_code
        == 200
    )
    assert client.put("/api/admin/settings", json={"signup_mode": "approval"}).status_code == 200
    assert client.post(f"/api/users/{target.json()['id']}/kick").status_code == 200
    assert client.post("/api/auth/logout").status_code == 204

    events = list(db.scalars(select(SecurityEvent).order_by(SecurityEvent.id)))
    event_types = {event.event_type for event in events}
    assert {
        "auth.login",
        "rbac.permission.created",
        "rbac.role.created",
        "user.role.changed",
        "user.permissions.changed",
        "settings.updated",
        "user.sessions.revoked",
        "auth.logout",
    } <= event_types
    permission_event = next(
        event for event in events if event.event_type == "rbac.permission.created"
    )
    assert permission_event.request_id == "audit-permission-create"
    encoded = json.dumps([event.event_metadata for event in events]).lower()
    assert "provider_subject" not in encoded
    assert "id_token" not in encoded
    assert "session_token" not in encoded
    assert all(db.get(SecurityEventDelivery, event.id) is not None for event in events)


def test_failed_login_is_audited_without_token_and_audit_failure_preserves_401(
    client, db, monkeypatch
):
    from app.routers import auth as auth_router

    def invalid_token(_token: str):
        raise ProviderTokenError

    monkeypatch.setattr(auth_router, "verify_google_id_token", invalid_token)
    response = client.post(
        "/api/auth/google",
        json={"id_token": "never-store-this-provider-token"},
        headers={"X-Request-ID": "denied-login"},
    )
    assert response.status_code == 401
    event = db.scalar(select(SecurityEvent).where(SecurityEvent.event_type == "auth.login"))
    assert event is not None
    assert event.outcome == "denied"
    assert event.request_id == "denied-login"
    assert event.event_metadata == {"provider": "google", "reason": "INVALID_TOKEN"}
    assert "never-store" not in json.dumps(event.event_metadata)

    def evidence_store_unavailable(*_args, **_kwargs):
        raise RuntimeError("outbox unavailable")

    monkeypatch.setattr(auth_router, "commit_security_event", evidence_store_unavailable)
    response = client.post("/api/auth/google", json={"id_token": "still-invalid"})
    assert response.status_code == 401
    assert response.json()["detail"]["code"] == "INVALID_TOKEN"


def test_security_event_cursor_filter_export_and_permissions(client, db):
    plain = client.post("/api/auth/dev", json={"email": "plain@example.com", "role": "user"})
    assert plain.status_code == 200
    assert client.get("/api/admin/security-events").status_code == 403

    admin = TestClient(client.app)
    assert (
        admin.post(
            "/api/auth/dev", json={"email": "admin@example.com", "role": "admin"}
        ).status_code
        == 200
    )
    for number in range(3):
        add_security_event(
            db,
            event_type="test.page",
            outcome="success",
            actor_user_id=admin.get("/api/auth/me").json()["id"],
            target_type="test_record",
            target_id=number,
            metadata={"number": number},
        )
    db.commit()

    first = admin.get("/api/admin/security-events", params={"event_type": "test.page", "limit": 2})
    assert first.status_code == 200
    assert len(first.json()["items"]) == 2
    cursor = first.json()["next_cursor"]
    assert cursor is not None
    second = admin.get(
        "/api/admin/security-events",
        params={"event_type": "test.page", "limit": 2, "before_id": cursor},
    )
    assert len(second.json()["items"]) == 1

    exported = admin.get(
        "/api/admin/security-events/export.csv",
        params={"event_type": "test.page", "limit": 10},
    )
    assert exported.status_code == 200
    assert exported.text.count("test.page") == 3
    db.expire_all()
    export_event = db.scalar(
        select(SecurityEvent).where(SecurityEvent.event_type == "security_events.exported")
    )
    assert export_event is not None
    assert export_event.event_metadata["exported_count"] == 3


def test_security_events_view_is_independent_from_request_log_access(client):
    assert (
        client.post(
            "/api/auth/dev", json={"email": "admin@example.com", "role": "admin"}
        ).status_code
        == 200
    )
    assert (
        client.post(
            "/api/roles", json={"name": "LogsOnly", "permission_keys": ["logs.view"]}
        ).status_code
        == 201
    )
    assert (
        client.post(
            "/api/roles",
            json={
                "name": "SecurityEventsOnly",
                "permission_keys": ["security_events.view"],
            },
        ).status_code
        == 201
    )

    logs_only = TestClient(client.app)
    logs_only.post("/api/auth/dev", json={"email": "logs-only@example.com", "role": "LogsOnly"})
    assert logs_only.get("/api/admin/logs").status_code == 200
    assert logs_only.get("/api/admin/security-events").status_code == 403

    security_only = TestClient(client.app)
    security_only.post(
        "/api/auth/dev",
        json={"email": "security-only@example.com", "role": "SecurityEventsOnly"},
    )
    assert security_only.get("/api/admin/security-events").status_code == 200
    assert security_only.get("/api/admin/logs").status_code == 403


def test_large_role_and_direct_grants_use_bounded_durable_summaries(client, db):
    admin = client.post(
        "/api/auth/dev", json={"email": "admin@example.com", "role": "admin"}
    ).json()
    target = (
        TestClient(client.app)
        .post("/api/auth/dev", json={"email": "large-grant-target@example.com", "role": "user"})
        .json()
    )
    keys = [f"catalog.permission{i:02d}" for i in range(60)]
    db.add_all(
        Permission(key=key, description=f"Catalog permission {index}", is_system=False)
        for index, key in enumerate(keys)
    )
    db.commit()

    role = client.post("/api/roles", json={"name": "LargeCatalogRole", "permission_keys": keys})
    direct = client.put(
        f"/api/users/{target['id']}/permissions",
        json={"permission_keys": keys, "expected_version": target["access_version"]},
    )

    assert role.status_code == 201, role.text
    assert {item["key"] for item in role.json()["permissions"]} == set(keys)
    assert direct.status_code == 200, direct.text
    assert set(direct.json()["direct_permissions"]) == set(keys)

    role_event = db.scalar(
        select(SecurityEvent).where(
            SecurityEvent.event_type == "rbac.role.created",
            SecurityEvent.target_id == str(role.json()["id"]),
        )
    )
    direct_event = db.scalar(
        select(SecurityEvent).where(
            SecurityEvent.event_type == "user.permissions.changed",
            SecurityEvent.target_id == str(target["id"]),
        )
    )
    assert role_event is not None and direct_event is not None
    assert role_event.actor_user_id == direct_event.actor_user_id == admin["id"]

    summaries = [
        role_event.event_metadata["permissions"],
        direct_event.event_metadata["after"]["permissions"],
    ]
    for summary in summaries:
        assert summary["count"] == 60
        assert len(summary["sha256"]) == 64
        assert len(summary["sample"]) == 8
        assert summary["sample_truncated"] is True
    assert keys[-1] not in json.dumps(summaries)
    assert len(json.dumps(role_event.event_metadata).encode()) < 4096
    assert len(json.dumps(direct_event.event_metadata).encode()) < 4096
    assert db.get(SecurityEventDelivery, role_event.id) is not None
    assert db.get(SecurityEventDelivery, direct_event.id) is not None


def test_unsafe_authenticated_denials_create_sanitized_durable_evidence(client, db):
    admin = client.post(
        "/api/auth/dev", json={"email": "admin@example.com", "role": "admin"}
    ).json()
    evidence_role = client.post(
        "/api/roles", json={"name": "EvidenceRole", "permission_keys": []}
    ).json()
    admin_role = next(item for item in client.get("/api/roles").json() if item["name"] == "admin")
    payload_secret = "never-store-this-denied-payload"

    forbidden = client.patch(
        f"/api/roles/{admin_role['id']}",
        json={"description": payload_secret},
        headers={"X-Request-ID": "denial-403"},
    )
    conflict = client.post(
        "/api/roles",
        json={
            "name": evidence_role["name"],
            "description": payload_secret,
            "permission_keys": [],
        },
        headers={"X-Request-ID": "denial-409"},
    )
    precondition = client.patch(
        f"/api/roles/{evidence_role['id']}",
        json={"description": payload_secret},
        headers={"X-Request-ID": "denial-428"},
    )

    assert forbidden.status_code == 403
    assert conflict.status_code == 409
    assert precondition.status_code == 428

    events = {
        event.request_id: event
        for event in db.scalars(
            select(SecurityEvent).where(SecurityEvent.event_type == "privileged_mutation.denied")
        )
    }
    assert set(events) == {"denial-403", "denial-409", "denial-428"}
    expected = {
        "denial-403": ("PATCH", 403, "PROTECTED_ROLE", "/roles/{role_id}"),
        "denial-409": ("POST", 409, "ALREADY_EXISTS", "/roles"),
        "denial-428": ("PATCH", 428, "PRECONDITION_REQUIRED", "/roles/{role_id}"),
    }
    for request_id, (method, status, code, route) in expected.items():
        event = events[request_id]
        assert event.actor_user_id == admin["id"]
        assert event.outcome == "denied"
        assert event.severity == "warning"
        assert event.target_type == "api_route"
        assert event.target_id == route
        assert event.event_metadata == {
            "method": method,
            "status_code": status,
            "code": code,
        }
        assert db.get(SecurityEventDelivery, event.id) is not None
    assert payload_secret not in json.dumps([event.event_metadata for event in events.values()])
