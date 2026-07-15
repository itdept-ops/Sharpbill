"""In-memory per-IP rate limiting (fixed window).

Per-process and in-memory, so it resets on restart and isn't shared across instances — a scaled
deploy would move this to shared storage (Redis) or an edge/WAF limiter. It exists to blunt the
obvious local abuse: credential-stuffing and CPU-burning token verification on the login routes.
"""

import threading
import time

_lock = threading.Lock()
_windows: dict[str, tuple[int, float]] = {}  # key -> (count, window_reset_monotonic)


def check(key: str, limit: int, window_seconds: float) -> int:
    """Register a hit for ``key``. Return 0 if allowed, else the Retry-After seconds until reset."""
    now = time.monotonic()
    with _lock:
        if len(_windows) > 10000:  # bound memory: sweep windows that have already reset
            for k in [k for k, (_, r) in _windows.items() if r <= now]:
                del _windows[k]
        count, reset_at = _windows.get(key, (0, 0.0))
        if now >= reset_at:
            count, reset_at = 0, now + window_seconds
        count += 1
        _windows[key] = (count, reset_at)
        if count > limit:
            return max(1, int(reset_at - now) + 1)
        return 0


def reset() -> None:
    """Clear all windows (used between tests)."""
    with _lock:
        _windows.clear()
