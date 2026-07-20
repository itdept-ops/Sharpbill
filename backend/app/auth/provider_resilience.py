"""Bounded, outage-tolerant identity-provider key retrieval.

Provider token verification runs in FastAPI's finite synchronous worker pool. This module keeps
both verification work and outbound key retrieval explicitly bounded, coalesces cache refreshes,
and applies negative/circuit backoff so arbitrary ``kid`` values cannot turn into an outbound
request oracle.
"""

from __future__ import annotations

import json
import logging
import threading
import time
from collections import OrderedDict
from collections.abc import Callable, Iterator, Mapping
from contextlib import contextmanager
from dataclasses import dataclass
from typing import Any

import requests

from app.auth import ProviderTokenError, ProviderUnavailableError
from app.config import settings

_log = logging.getLogger(__name__)


class _KeyFetchError(Exception):
    """A normalized key-endpoint transport or document failure."""


class NonBlockingCapacity:
    """A fail-fast concurrency gate; callers never queue behind saturated verification work."""

    def __init__(self, capacity: int, label: str) -> None:
        self._semaphore = threading.BoundedSemaphore(capacity)
        self._label = label

    @contextmanager
    def slot(self) -> Iterator[None]:
        if not self._semaphore.acquire(blocking=False):
            raise ProviderUnavailableError(f"{self._label} capacity is exhausted")
        try:
            yield
        finally:
            self._semaphore.release()


@dataclass(frozen=True)
class KeyCachePolicy:
    ttl_seconds: float
    stale_seconds: float
    refresh_wait_seconds: float
    unknown_kid_backoff_seconds: float
    outage_backoff_initial_seconds: float
    outage_backoff_max_seconds: float
    negative_cache_capacity: int = 512


@dataclass(frozen=True)
class _CacheView:
    document: Mapping[str, Any] | None
    known: bool
    age: float


@dataclass(frozen=True)
class _RefreshPlan:
    cached_document: Mapping[str, Any] | None
    fallback_document: Mapping[str, Any] | None
    fallback_known: bool


def _runtime_policy() -> KeyCachePolicy:
    return KeyCachePolicy(
        ttl_seconds=settings.idp_key_cache_ttl_seconds,
        stale_seconds=settings.idp_key_cache_stale_seconds,
        refresh_wait_seconds=settings.idp_key_refresh_wait_seconds,
        unknown_kid_backoff_seconds=settings.idp_unknown_kid_backoff_seconds,
        outage_backoff_initial_seconds=settings.idp_outage_backoff_initial_seconds,
        outage_backoff_max_seconds=settings.idp_outage_backoff_max_seconds,
    )


class ProviderKeyCache:
    """Thread-safe provider key cache with single-flight refresh and bounded backoff."""

    def __init__(
        self,
        *,
        provider: str,
        fetch_document: Callable[[], Mapping[str, Any]],
        key_ids: Callable[[Mapping[str, Any]], set[str]],
        policy: Callable[[], KeyCachePolicy] = _runtime_policy,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._provider = provider
        self._fetch_document = fetch_document
        self._extract_key_ids = key_ids
        self._policy = policy
        self._clock = clock
        self._condition = threading.Condition()
        self._document: Mapping[str, Any] | None = None
        self._document_key_ids: set[str] = set()
        self._fetched_at = 0.0
        self._refreshing = False
        self._failure_count = 0
        self._circuit_until = 0.0
        self._unknown_refresh_block_until = 0.0
        self._negative_kids: OrderedDict[str, float] = OrderedDict()

    def document_for_kid(self, kid: str) -> Mapping[str, Any]:
        """Return a key document containing ``kid``, refreshing at most once when needed."""
        self._validate_kid(kid)
        policy = self._policy()
        plan = self._plan_refresh(kid, policy)
        if plan.cached_document is not None:
            return plan.cached_document

        try:
            refreshed, refreshed_ids = self._fetch_validated_document()
        except (ProviderUnavailableError, _KeyFetchError) as exc:
            return self._record_refresh_failure(plan, policy, exc)
        except Exception as exc:
            # A provider parser/library regression must not strand `_refreshing=True`, which
            # would turn one bad document into a permanent waiter pile-up. Treat it as an
            # upstream failure for circuit/backoff purposes and keep the stack trace visible.
            _log.exception("Unexpected %s signing-key refresh failure", self._provider)
            return self._record_refresh_failure(plan, policy, exc)

        return self._record_refresh_success(kid, refreshed, refreshed_ids, policy)

    @staticmethod
    def _validate_kid(kid: str) -> None:
        if not kid or len(kid) > 256 or any(ord(char) < 0x20 for char in kid):
            raise ProviderTokenError("missing or malformed signing key id")

    def _plan_refresh(self, kid: str, policy: KeyCachePolicy) -> _RefreshPlan:
        wait_deadline = time.monotonic() + policy.refresh_wait_seconds
        while True:
            with self._condition:
                now = self._clock()
                self._prune_negative_kids(now)
                view = self._cache_view(kid, now)
                cached = self._usable_cached_document(view, now, policy)
                if cached is not None:
                    return _RefreshPlan(cached, None, False)
                if not self._refreshing:
                    self._enforce_unknown_kid_backoff(kid, view.known, now, policy)
                    self._refreshing = True
                    return _RefreshPlan(None, view.document, view.known)
                self._wait_for_refresh(wait_deadline)

    def _cache_view(self, kid: str, now: float) -> _CacheView:
        document = self._document
        known = document is not None and kid in self._document_key_ids
        age = now - self._fetched_at if document is not None else float("inf")
        return _CacheView(document, known, age)

    def _usable_cached_document(
        self, view: _CacheView, now: float, policy: KeyCachePolicy
    ) -> Mapping[str, Any] | None:
        if view.known and view.age <= policy.ttl_seconds:
            assert view.document is not None
            return view.document
        if now >= self._circuit_until:
            return None
        if view.known and view.age <= policy.stale_seconds:
            assert view.document is not None
            return view.document
        raise ProviderUnavailableError(f"{self._provider} signing-key endpoint is unavailable")

    def _enforce_unknown_kid_backoff(
        self, kid: str, known: bool, now: float, policy: KeyCachePolicy
    ) -> None:
        negative_until = self._negative_kids.get(kid, 0.0)
        if known or (now >= negative_until and now >= self._unknown_refresh_block_until):
            return
        self._remember_negative_kid(
            kid,
            max(negative_until, self._unknown_refresh_block_until),
            policy.negative_cache_capacity,
        )
        raise ProviderTokenError("unknown signing key id")

    def _wait_for_refresh(self, deadline: float) -> None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ProviderUnavailableError(f"{self._provider} signing-key refresh timed out")
        self._condition.wait(timeout=remaining)

    def _fetch_validated_document(self) -> tuple[Mapping[str, Any], set[str]]:
        document = self._fetch_document()
        key_ids = self._extract_key_ids(document)
        if not key_ids:
            raise _KeyFetchError("provider returned an empty key document")
        return document, key_ids

    def _record_refresh_failure(
        self,
        plan: _RefreshPlan,
        policy: KeyCachePolicy,
        exc: Exception,
    ) -> Mapping[str, Any]:
        with self._condition:
            now = self._clock()
            self._failure_count += 1
            exponent = min(self._failure_count - 1, 16)
            delay = min(
                policy.outage_backoff_initial_seconds * (2**exponent),
                policy.outage_backoff_max_seconds,
            )
            self._circuit_until = now + delay
            self._refreshing = False
            self._condition.notify_all()
            stale_age = now - self._fetched_at
            if (
                plan.fallback_known
                and plan.fallback_document is not None
                and stale_age <= policy.stale_seconds
            ):
                return plan.fallback_document
        raise ProviderUnavailableError(f"{self._provider} signing keys are unavailable") from exc

    def _record_refresh_success(
        self,
        kid: str,
        refreshed: Mapping[str, Any],
        refreshed_ids: set[str],
        policy: KeyCachePolicy,
    ) -> Mapping[str, Any]:
        with self._condition:
            now = self._clock()
            self._document = refreshed
            self._document_key_ids = refreshed_ids
            self._fetched_at = now
            self._failure_count = 0
            self._circuit_until = 0.0
            self._refreshing = False
            if kid not in refreshed_ids:
                blocked_until = now + policy.unknown_kid_backoff_seconds
                self._unknown_refresh_block_until = blocked_until
                self._remember_negative_kid(kid, blocked_until, policy.negative_cache_capacity)
            self._condition.notify_all()

            if kid not in refreshed_ids:
                raise ProviderTokenError("unknown signing key id")
            return refreshed

    def _prune_negative_kids(self, now: float) -> None:
        expired = [kid for kid, expiry in self._negative_kids.items() if expiry <= now]
        for kid in expired:
            self._negative_kids.pop(kid, None)

    def _remember_negative_kid(self, kid: str, expiry: float, capacity: int) -> None:
        self._negative_kids.pop(kid, None)
        self._negative_kids[kid] = expiry
        while len(self._negative_kids) > capacity:
            self._negative_kids.popitem(last=False)


_verification_capacity = NonBlockingCapacity(
    settings.idp_verification_max_concurrency, "identity-provider verification"
)
_network_capacity = NonBlockingCapacity(
    settings.idp_network_max_concurrency, "identity-provider key retrieval"
)


@contextmanager
def verification_slot() -> Iterator[None]:
    with _verification_capacity.slot():
        yield


def fetch_json_document(url: str) -> Mapping[str, Any]:
    """Fetch one bounded JSON key document using explicit connect/read timeouts."""
    try:
        with _network_capacity.slot():
            with requests.get(
                url,
                headers={"Accept": "application/json", "User-Agent": "kingfisher-crm/1"},
                timeout=(
                    settings.idp_http_connect_timeout_seconds,
                    settings.idp_http_read_timeout_seconds,
                ),
                stream=True,
                allow_redirects=False,
            ) as response:
                if response.status_code != 200:
                    raise _KeyFetchError(f"provider key endpoint returned {response.status_code}")
                payload = bytearray()
                for chunk in response.iter_content(chunk_size=65_536):
                    payload.extend(chunk)
                    if len(payload) > settings.idp_key_document_max_bytes:
                        raise _KeyFetchError("provider key document exceeded the size limit")
    except requests.RequestException as exc:
        raise _KeyFetchError("provider key endpoint request failed") from exc

    try:
        parsed = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as exc:
        raise _KeyFetchError("provider returned an invalid key document") from exc
    if not isinstance(parsed, dict):
        raise _KeyFetchError("provider key document must be a JSON object")
    return parsed


def google_certificate_ids(document: Mapping[str, Any]) -> set[str]:
    if "keys" in document:
        raise _KeyFetchError("Google certificate endpoint returned an unexpected JWKS document")
    if not all(isinstance(kid, str) and isinstance(cert, str) for kid, cert in document.items()):
        raise _KeyFetchError("Google certificate document is malformed")
    return set(document)


def microsoft_jwks_ids(document: Mapping[str, Any]) -> set[str]:
    keys = document.get("keys")
    if not isinstance(keys, list):
        raise _KeyFetchError("Microsoft JWKS document is malformed")
    ids: set[str] = set()
    for key in keys:
        if not isinstance(key, dict) or not isinstance(key.get("kid"), str):
            raise _KeyFetchError("Microsoft JWKS key is malformed")
        ids.add(key["kid"])
    return ids
