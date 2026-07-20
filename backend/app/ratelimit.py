"""In-memory per-IP rate limiting (fixed window).

Per-process and in-memory, so it resets on restart and isn't shared across instances — a scaled
deploy would move this to shared storage (Redis) or an edge/WAF limiter. It exists to blunt the
obvious local abuse: credential-stuffing and CPU-burning token verification on the login routes.
"""

import threading
import time
from heapq import heappop, heappush
from math import ceil

_lock = threading.Lock()
_windows: dict[str, tuple[int, float]] = {}  # key -> (count, window_reset_monotonic)
_expiries: list[tuple[float, str]] = []
_MAX_WINDOWS = 10_000


def _prune_expired(now: float) -> None:
    while _expiries and _expiries[0][0] <= now:
        reset_at, key = heappop(_expiries)
        current = _windows.get(key)
        if current is not None and current[1] == reset_at:
            del _windows[key]


def check(key: str, limit: int, window_seconds: float) -> int:
    """Register a hit for ``key``. Return 0 if allowed, else the Retry-After seconds until reset."""
    now = time.monotonic()
    with _lock:
        _prune_expired(now)
        if key not in _windows and len(_windows) >= _MAX_WINDOWS:
            # Fail closed for a new cardinality key without inserting it. The expiry heap keeps
            # cleanup O(log n), avoiding a 10k-entry scan on each attacker-generated IP.
            return max(1, ceil(window_seconds))
        count, reset_at = _windows.get(key, (0, 0.0))
        if now >= reset_at:
            count, reset_at = 0, now + window_seconds
            heappush(_expiries, (reset_at, key))
        count += 1
        _windows[key] = (count, reset_at)
        if count > limit:
            return max(1, int(reset_at - now) + 1)
        return 0


def reset() -> None:
    """Clear all windows (used between tests)."""
    with _lock:
        _windows.clear()
        _expiries.clear()
