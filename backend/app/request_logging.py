import json
import logging
import queue
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Any

from fastapi import Request
from sqlalchemy import delete, select

from app.auth.jwt import COOKIE_NAME, decode_session_token
from app.config import settings
from app.db import SessionLocal
from app.models import RequestLog

_log = logging.getLogger("app.requests")

# Frequent/noisy paths we don't persist (health checks, the WS, docs, polling, session probes).
_SKIP_PREFIXES = (
    "/api/health",
    "/api/ws",
    "/api/docs",
    "/api/openapi",
    "/api/presence",
    "/api/auth/config",
    "/api/auth/me",
    "/api/auth/nonce",
)


def _should_log(method: str, path: str) -> bool:
    if method == "OPTIONS" or not path.startswith("/api"):
        return False
    return not any(path.startswith(prefix) for prefix in _SKIP_PREFIXES)


def _client_ip(request: Request) -> str | None:
    # ProxyHeadersMiddleware rewrites this only when the socket peer is explicitly trusted.
    return request.client.host if request.client else None


def _user_id(request: Request) -> int | None:
    uid = getattr(request.state, "user_id", None)
    if uid is not None:
        return int(uid)
    token = request.cookies.get(COOKIE_NAME)
    if not token:
        return None
    try:
        return int(decode_session_token(token)["sub"])
    except Exception:
        return None


def prune_request_logs(
    db,
    older_than_days: int | None = None,
    limit: int | None = None,
) -> int:
    """Delete one indexed, bounded batch of access telemetry past retention."""
    retention_days = (
        settings.request_log_retention_days if older_than_days is None else older_than_days
    )
    cutoff = datetime.now(UTC).replace(tzinfo=None) - timedelta(days=retention_days)
    batch_size = limit or settings.request_log_prune_batch_size
    # InnoDB carries the primary key in each secondary-index record, so the existing created_at
    # index serves this deterministic (created_at, id) walk without another write-heavy index.
    stale_ids = list(
        db.scalars(
            select(RequestLog.id)
            .where(RequestLog.created_at < cutoff)
            .order_by(RequestLog.created_at, RequestLog.id)
            .limit(batch_size)
        )
    )
    if not stale_ids:
        return 0
    result = db.execute(delete(RequestLog).where(RequestLog.id.in_(stale_ids)))
    db.commit()
    return result.rowcount or 0


@dataclass(frozen=True)
class RequestLogRecord:
    method: str
    path: str
    user_id: int | None
    ip: str | None
    status_code: int


@dataclass(frozen=True)
class _Flush:
    completed: threading.Event


@dataclass(frozen=True)
class _Stop:
    pass


QueueItem = RequestLogRecord | _Flush | _Stop
PersistFn = Callable[[RequestLogRecord], None]


def _persist_record(record: RequestLogRecord) -> None:
    with SessionLocal() as db:
        db.add(
            RequestLog(
                method=record.method,
                path=record.path,
                user_id=record.user_id,
                ip=record.ip,
                status_code=record.status_code,
            )
        )
        db.commit()


class RequestLogSink:
    """Single-writer bounded queue that keeps telemetry off response latency.

    Saturation never blocks a request: a structured loss signal and counter make the tradeoff
    explicit. Shutdown places a barrier, drains accepted records within a deadline, then joins
    the writer cleanly.
    """

    def __init__(self, capacity: int, persist: PersistFn = _persist_record) -> None:
        self.capacity = capacity
        self._persist = persist
        self._queue: queue.Queue[QueueItem] = queue.Queue(maxsize=capacity)
        self._lifecycle_lock = threading.Lock()
        self._metrics_lock = threading.Lock()
        self._thread: threading.Thread | None = None
        self._stopping = False
        self._backpressure_active = False
        self._enqueued = 0
        self._persisted = 0
        self._dropped = 0
        self._errors = 0

    def _start_locked(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._stopping = False
        self._thread = threading.Thread(
            target=self._run,
            name="request-log-writer",
            daemon=True,
        )
        self._thread.start()

    def start(self) -> None:
        with self._lifecycle_lock:
            self._start_locked()

    def _structured_signal(self, event: str, **fields: Any) -> None:
        _log.warning(
            "%s",
            json.dumps({"event": event, **fields}, separators=(",", ":")),
        )

    def _record_drop(self, reason: str) -> None:
        with self._metrics_lock:
            self._dropped += 1
            dropped = self._dropped
        self._structured_signal(
            "request_log_dropped",
            reason=reason,
            dropped_total=dropped,
            queue_depth=self._queue.qsize(),
            queue_capacity=self.capacity,
        )

    def enqueue(self, record: RequestLogRecord) -> bool:
        with self._lifecycle_lock:
            if self._stopping:
                self._record_drop("shutting_down")
                return False
            self._start_locked()
            try:
                self._queue.put_nowait(record)
            except queue.Full:
                self._record_drop("queue_full")
                return False
        depth = self._queue.qsize()
        with self._metrics_lock:
            self._enqueued += 1
            enqueued = self._enqueued
            should_signal = depth >= max(1, int(self.capacity * 0.75))
            if should_signal and not self._backpressure_active:
                self._backpressure_active = True
                signal = True
            else:
                signal = False
        if signal:
            self._structured_signal(
                "request_log_backpressure",
                queue_depth=depth,
                queue_capacity=self.capacity,
                enqueued_total=enqueued,
            )
        return True

    def _run(self) -> None:
        while True:
            item = self._queue.get()
            try:
                if isinstance(item, _Stop):
                    return
                if isinstance(item, _Flush):
                    item.completed.set()
                    continue
                try:
                    self._persist(item)
                except Exception:
                    with self._metrics_lock:
                        self._errors += 1
                    _log.exception("failed to persist request log")
                else:
                    with self._metrics_lock:
                        self._persisted += 1
            finally:
                depth = self._queue.qsize()
                with self._metrics_lock:
                    if depth < max(1, int(self.capacity * 0.5)):
                        self._backpressure_active = False
                self._queue.task_done()

    def flush(self, timeout: float = 5.0) -> bool:
        """Wait until every record accepted before this call has been processed."""
        with self._lifecycle_lock:
            if self._thread is None and self._queue.empty():
                return True
            if not self._stopping:
                self._start_locked()
        barrier = _Flush(threading.Event())
        try:
            self._queue.put(barrier, timeout=max(0.0, timeout))
        except queue.Full:
            return False
        return barrier.completed.wait(timeout=max(0.0, timeout))

    def shutdown(self, timeout: float = 5.0) -> bool:
        started = time.monotonic()
        with self._lifecycle_lock:
            thread = self._thread
            if thread is None:
                return True
            self._stopping = True
        drained = self.flush(timeout)
        remaining = max(0.0, timeout - (time.monotonic() - started))
        try:
            self._queue.put(_Stop(), timeout=remaining)
        except queue.Full:
            drained = False
        remaining = max(0.0, timeout - (time.monotonic() - started))
        thread.join(remaining)
        stopped = not thread.is_alive()
        if not drained or not stopped:
            self._structured_signal(
                "request_log_shutdown_timeout",
                queue_depth=self._queue.qsize(),
                queue_capacity=self.capacity,
            )
            return False
        with self._lifecycle_lock:
            self._thread = None
            self._stopping = False
        return True

    def metrics(self) -> dict[str, int | bool]:
        with self._metrics_lock:
            counters = {
                "enqueued_total": self._enqueued,
                "persisted_total": self._persisted,
                "dropped_total": self._dropped,
                "errors_total": self._errors,
            }
        thread = self._thread
        return {
            **counters,
            "queue_depth": self._queue.qsize(),
            "queue_capacity": self.capacity,
            "running": bool(thread and thread.is_alive()),
        }


request_log_sink = RequestLogSink(settings.request_log_queue_capacity)


def start_request_logging() -> None:
    request_log_sink.start()


def shutdown_request_logging() -> bool:
    return request_log_sink.shutdown(settings.request_log_shutdown_timeout_seconds)


def flush_request_logging(timeout: float = 5.0) -> bool:
    return request_log_sink.flush(timeout)


def request_logging_metrics() -> dict[str, int | bool]:
    return request_log_sink.metrics()


def record_request(request: Request, status_code: int) -> bool:
    """Emit structured stdout synchronously, then enqueue bounded DB persistence."""
    method, path = request.method, request.url.path
    if not _should_log(method, path):
        return False
    ip = _client_ip(request)
    uid = _user_id(request)
    started_at = getattr(request.state, "started_at", None)
    duration_ms = (
        round((time.perf_counter() - started_at) * 1000, 2) if started_at is not None else None
    )
    _log.info(
        "%s",
        json.dumps(
            {
                "event": "http_request",
                "request_id": getattr(request.state, "request_id", None),
                "method": method,
                "path": path,
                "status_code": status_code,
                "duration_ms": duration_ms,
                "user_id": uid,
                "client_ip": ip,
            },
            separators=(",", ":"),
        ),
    )
    return request_log_sink.enqueue(
        RequestLogRecord(
            method=method[:10],
            path=path[:255],
            user_id=uid,
            ip=(ip[:45] if ip else None),
            status_code=status_code,
        )
    )
