"""Scheduled retention runs independently of log traffic and user sign-in."""

import threading
import time
from datetime import UTC, datetime, timedelta

from sqlalchemy import func, select

from app.config import settings
from app.models import RequestLog, UserSession
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
