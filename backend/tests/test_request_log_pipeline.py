import threading
import time

from app.request_logging import RequestLogRecord, RequestLogSink


def _record(path: str) -> RequestLogRecord:
    return RequestLogRecord(
        method="GET",
        path=path,
        user_id=None,
        ip="127.0.0.1",
        status_code=200,
    )


def test_bounded_sink_never_blocks_request_and_reports_loss_under_backpressure():
    entered = threading.Event()
    release = threading.Event()
    persisted: list[str] = []

    def slow_persist(record: RequestLogRecord) -> None:
        entered.set()
        assert release.wait(2)
        persisted.append(record.path)

    sink = RequestLogSink(capacity=1, persist=slow_persist)
    try:
        assert sink.enqueue(_record("/api/first"))
        assert entered.wait(1)
        assert sink.enqueue(_record("/api/second"))

        started = time.perf_counter()
        assert not sink.enqueue(_record("/api/dropped"))
        assert time.perf_counter() - started < 0.1
        metrics = sink.metrics()
        assert metrics["dropped_total"] == 1
        assert metrics["queue_capacity"] == 1
    finally:
        release.set()

    assert sink.shutdown(timeout=2)
    assert persisted == ["/api/first", "/api/second"]
    assert sink.metrics()["persisted_total"] == 2
    assert sink.metrics()["running"] is False


def test_sink_counts_persistence_errors_and_continues_draining():
    attempted: list[str] = []

    def sometimes_fails(record: RequestLogRecord) -> None:
        attempted.append(record.path)
        if record.path.endswith("bad"):
            raise RuntimeError("test persistence failure")

    sink = RequestLogSink(capacity=4, persist=sometimes_fails)
    assert sink.enqueue(_record("/api/bad"))
    assert sink.enqueue(_record("/api/good"))
    assert sink.shutdown(timeout=2)

    assert attempted == ["/api/bad", "/api/good"]
    assert sink.metrics()["errors_total"] == 1
    assert sink.metrics()["persisted_total"] == 1


def test_log_pipeline_metrics_require_log_permission(client):
    client.post("/api/auth/dev", json={"email": "plain@example.com", "role": "user"})
    assert client.get("/api/admin/logs/metrics").status_code == 403

    client.post("/api/auth/dev", json={"email": "admin@example.com", "role": "admin"})
    response = client.get("/api/admin/logs/metrics")
    assert response.status_code == 200
    assert {
        "enqueued_total",
        "persisted_total",
        "dropped_total",
        "errors_total",
        "queue_depth",
        "queue_capacity",
        "running",
    } == set(response.json())
