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
from heapq import heappop, heappush

_lock = threading.Lock()
_seen: dict[str, float] = {}  # sha256(token) -> monotonic expiry
_expiries: list[tuple[float, str]] = []
_MAX_SEEN = 10_000


def _prune_expired(now: float) -> None:
    """Discard expired entries in O(log n) time without scanning the active set."""
    while _expiries and _expiries[0][0] <= now:
        expires_at, key = heappop(_expiries)
        if _seen.get(key) == expires_at:
            del _seen[key]


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
        _prune_expired(now)
        if key in _seen:
            return True
        if len(_seen) >= _MAX_SEEN:
            # Preserve the hard memory boundary under attacker-controlled token cardinality.
            # Treat an unrecordable token as unsafe (the caller rejects True) rather than
            # silently weakening replay protection until another entry expires.
            return True
        expires_at = now + ttl_seconds
        _seen[key] = expires_at
        heappush(_expiries, (expires_at, key))
        return False


def _reset_for_tests() -> None:
    with _lock:
        _seen.clear()
        _expiries.clear()
