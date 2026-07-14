"""In-memory single-use guard for provider ID tokens (defense-in-depth against replay).

A verified id_token is recorded by hash until it expires; presenting the *same* token again within
its validity window is rejected. Distinct tokens (a normal re-login mints a fresh one) are
unaffected. This closes the trivial "replay a captured token" window with no external dependency.

Scope/limits: per-process and in-memory, so it resets on restart and is not shared across multiple
API instances — a horizontally-scaled deployment would move this to shared storage (e.g. Redis).
It is a complement to, not a replacement for, provider nonce binding (which needs the live OAuth
client flow).
"""

import hashlib
import threading
import time

_lock = threading.Lock()
_seen: dict[str, float] = {}  # sha256(token) -> monotonic expiry


def check_replay(raw_token: str, ttl_seconds: float) -> bool:
    """Return True if ``raw_token`` was already presented (a replay).

    Otherwise record it for ``ttl_seconds`` and return False. A non-positive TTL (token already
    expired) is treated as not-seen and never recorded.
    """
    if ttl_seconds <= 0:
        return False
    key = hashlib.sha256(raw_token.encode("utf-8")).hexdigest()
    now = time.monotonic()
    with _lock:
        if _seen:  # opportunistic prune of expired entries
            for stale in [k for k, exp in _seen.items() if exp <= now]:
                del _seen[stale]
        if key in _seen:
            return True
        _seen[key] = now + ttl_seconds
        return False


def _reset_for_tests() -> None:
    with _lock:
        _seen.clear()
