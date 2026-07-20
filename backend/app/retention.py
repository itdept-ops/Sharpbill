"""Bounded, periodic database-retention maintenance.

The API must not rely on fresh logins or access-log writes to trigger cleanup. This worker waits
for a full interval before its first cycle, then deletes only a configured number of bounded
batches. The startup delay keeps schema migration/readiness ownership outside application startup.
"""

import json
import logging
import threading
from collections.abc import Callable
from dataclasses import asdict, dataclass

from sqlalchemy.orm import Session

from app.auth.sessions import prune_stale_sessions
from app.config import settings
from app.db import SessionLocal
from app.request_logging import prune_request_logs

_log = logging.getLogger("app.retention")

SessionFactory = Callable[[], Session]
PruneBatch = Callable[[Session, int], int]


@dataclass(frozen=True)
class RetentionResult:
    request_logs_deleted: int
    request_log_batches: int
    sessions_deleted: int
    session_batches: int


def _drain_bounded(
    session_factory: SessionFactory,
    prune_batch: PruneBatch,
    *,
    batch_size: int,
    max_batches: int,
) -> tuple[int, int]:
    """Run independently committed batches and stop as soon as a batch is not full."""
    total = 0
    batches = 0
    for _ in range(max_batches):
        with session_factory() as db:
            deleted = prune_batch(db, batch_size)
            db.commit()
        batches += 1
        total += deleted
        if deleted < batch_size:
            break
    return total, batches


def run_retention_cycle(
    *,
    session_factory: SessionFactory | None = None,
    request_log_batch_size: int | None = None,
    session_batch_size: int | None = None,
    max_batches: int | None = None,
) -> RetentionResult:
    """Delete at most ``batch_size * max_batches`` rows from each retained table."""
    factory = session_factory or SessionLocal
    log_batch_size = (
        settings.request_log_prune_batch_size
        if request_log_batch_size is None
        else request_log_batch_size
    )
    user_session_batch_size = (
        settings.session_prune_batch_size if session_batch_size is None else session_batch_size
    )
    cycle_batch_limit = (
        settings.retention_worker_max_batches_per_cycle if max_batches is None else max_batches
    )
    if min(log_batch_size, user_session_batch_size, cycle_batch_limit) < 1:
        raise ValueError("retention batch sizes and cycle limit must be positive")

    def prune_logs(db: Session, limit: int) -> int:
        return prune_request_logs(
            db,
            older_than_days=settings.request_log_retention_days,
            limit=limit,
        )

    def prune_sessions(db: Session, limit: int) -> int:
        return prune_stale_sessions(db, limit=limit)

    logs_deleted, log_batches = _drain_bounded(
        factory,
        prune_logs,
        batch_size=log_batch_size,
        max_batches=cycle_batch_limit,
    )
    sessions_deleted, session_batches = _drain_bounded(
        factory,
        prune_sessions,
        batch_size=user_session_batch_size,
        max_batches=cycle_batch_limit,
    )
    result = RetentionResult(
        request_logs_deleted=logs_deleted,
        request_log_batches=log_batches,
        sessions_deleted=sessions_deleted,
        session_batches=session_batches,
    )
    _log.info(
        "%s",
        json.dumps({"event": "retention_cycle", **asdict(result)}, separators=(",", ":")),
    )
    return result


class RetentionWorker:
    """Single-process scheduler with delayed first run and truly bounded shutdown.

    A dedicated daemon thread keeps synchronous SQL off the event loop. Unlike cancellation of
    ``asyncio.to_thread``, a timed-out join does not make a false claim that the SQL thread stopped
    and cannot hold process exit open indefinitely.
    """

    def __init__(
        self,
        *,
        interval_seconds: float,
        shutdown_timeout_seconds: float,
        run_cycle: Callable[[], object] = run_retention_cycle,
    ) -> None:
        if interval_seconds <= 0 or shutdown_timeout_seconds <= 0:
            raise ValueError("retention worker intervals must be positive")
        self._interval_seconds = interval_seconds
        self._shutdown_timeout_seconds = shutdown_timeout_seconds
        self._run_cycle = run_cycle
        self._lifecycle_lock = threading.Lock()
        self._stop_event: threading.Event | None = None
        self._thread: threading.Thread | None = None

    def _serve(self, stop_event: threading.Event) -> None:
        # Event.wait provides both the delayed first run and an interruptible periodic sleep.
        while not stop_event.wait(self._interval_seconds):
            try:
                self._run_cycle()
            except Exception:
                # A transient DB/schema failure must not terminate future maintenance cycles.
                _log.exception("retention cycle failed")

    def start(self) -> None:
        with self._lifecycle_lock:
            if self._thread is not None and self._thread.is_alive():
                return
            stop_event = threading.Event()
            self._stop_event = stop_event
            self._thread = threading.Thread(
                target=self._serve,
                args=(stop_event,),
                name="database-retention",
                daemon=True,
            )
            self._thread.start()

    def shutdown(self) -> bool:
        with self._lifecycle_lock:
            thread = self._thread
            stop_event = self._stop_event
            if thread is None or stop_event is None:
                return True
        stop_event.set()
        thread.join(self._shutdown_timeout_seconds)
        stopped = not thread.is_alive()
        if not stopped:
            _log.warning(
                "%s",
                json.dumps(
                    {"event": "retention_shutdown_timeout"},
                    separators=(",", ":"),
                ),
            )
            return False
        with self._lifecycle_lock:
            if self._thread is thread:
                self._thread = None
                self._stop_event = None
        return stopped


retention_worker = RetentionWorker(
    interval_seconds=settings.retention_worker_interval_seconds,
    shutdown_timeout_seconds=settings.retention_worker_shutdown_timeout_seconds,
)


def start_retention_worker() -> None:
    retention_worker.start()


def shutdown_retention_worker() -> bool:
    return retention_worker.shutdown()
