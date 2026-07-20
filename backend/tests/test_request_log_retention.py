"""Audit-log retention: rows older than the window are pruned (FND-013)."""

from datetime import UTC, datetime, timedelta

from app.models import RequestLog
from app.request_logging import prune_request_logs


def test_prune_removes_old_rows_keeps_recent(db):
    old = [
        RequestLog(
            method="GET",
            path=f"/api/old-{number}",
            user_id=None,
            ip="127.0.0.1",
            status_code=200,
            created_at=datetime.now(UTC).replace(tzinfo=None) - timedelta(days=200),
        )
        for number in range(3)
    ]
    recent = RequestLog(
        method="GET",
        path="/api/recent",
        user_id=None,
        ip="127.0.0.1",
        status_code=200,
    )
    db.add_all([*old, recent])
    db.commit()

    # Each writer-side maintenance transaction is capped even if a long-dormant deployment has
    # a large backlog. Repeated invocations make monotonic progress.
    assert prune_request_logs(db, older_than_days=90, limit=2) == 2
    assert prune_request_logs(db, older_than_days=90, limit=2) == 1
    assert prune_request_logs(db, older_than_days=90, limit=2) == 0

    remaining_paths = {r.path for r in db.query(RequestLog).all()}
    assert "/api/recent" in remaining_paths
    assert not any(path.startswith("/api/old-") for path in remaining_paths)
