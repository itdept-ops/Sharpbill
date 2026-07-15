"""Audit-log retention: rows older than the window are pruned (FND-013)."""

from datetime import UTC, datetime, timedelta

from app.models import RequestLog
from app.request_logging import prune_request_logs


def test_prune_removes_old_rows_keeps_recent(db):
    old = RequestLog(
        method="GET",
        path="/api/old",
        user_id=None,
        ip="127.0.0.1",
        status_code=200,
        created_at=datetime.now(UTC).replace(tzinfo=None) - timedelta(days=200),
    )
    recent = RequestLog(
        method="GET",
        path="/api/recent",
        user_id=None,
        ip="127.0.0.1",
        status_code=200,
    )
    db.add_all([old, recent])
    db.commit()

    removed = prune_request_logs(db, older_than_days=90)
    assert removed >= 1

    remaining_paths = {r.path for r in db.query(RequestLog).all()}
    assert "/api/recent" in remaining_paths
    assert "/api/old" not in remaining_paths
