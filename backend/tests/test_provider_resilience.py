"""Bounded identity-provider verification and key-refresh behavior."""

from __future__ import annotations

import threading
from collections.abc import Iterator, Mapping
from concurrent.futures import ThreadPoolExecutor
from typing import Any

import pytest
import requests

from app.auth import ProviderTokenError, ProviderUnavailableError, provider_resilience
from app.auth import google as google_auth
from app.auth.provider_resilience import (
    KeyCachePolicy,
    NonBlockingCapacity,
    ProviderKeyCache,
    fetch_json_document,
)
from app.config import settings
from app.main import app
from tests.client import TestClient


def _policy(**overrides: float) -> KeyCachePolicy:
    values: dict[str, float | int] = {
        "ttl_seconds": 10.0,
        "stale_seconds": 100.0,
        "refresh_wait_seconds": 2.0,
        "unknown_kid_backoff_seconds": 10.0,
        "outage_backoff_initial_seconds": 5.0,
        "outage_backoff_max_seconds": 20.0,
        "negative_cache_capacity": 4,
    }
    values.update(overrides)
    return KeyCachePolicy(**values)  # type: ignore[arg-type]


def _certificate_ids(document: Mapping[str, Any]) -> set[str]:
    return set(document)


class _FakeResponse:
    status_code = 200

    def __init__(self, chunks: list[bytes]) -> None:
        self._chunks = chunks

    def __enter__(self) -> _FakeResponse:
        return self

    def __exit__(self, *_args: object) -> None:
        return None

    def iter_content(self, *, chunk_size: int) -> Iterator[bytes]:
        assert chunk_size == 65_536
        yield from self._chunks


def test_key_fetch_uses_explicit_short_timeouts_and_no_redirects(monkeypatch):
    observed: dict[str, Any] = {}

    def fake_get(url: str, **kwargs: Any) -> _FakeResponse:
        observed.update(url=url, **kwargs)
        return _FakeResponse([b'{"kid-1":"certificate"}'])

    monkeypatch.setattr("app.auth.provider_resilience.requests.get", fake_get)
    document = fetch_json_document("https://idp.example.test/keys")

    assert document == {"kid-1": "certificate"}
    assert observed["timeout"] == (
        settings.idp_http_connect_timeout_seconds,
        settings.idp_http_read_timeout_seconds,
    )
    assert observed["stream"] is True
    assert observed["allow_redirects"] is False


def test_network_timeout_opens_circuit_and_fails_fast(monkeypatch):
    calls = 0
    now = [100.0]

    def timeout(*_args: Any, **_kwargs: Any):
        nonlocal calls
        calls += 1
        raise requests.ConnectTimeout("simulated timeout")

    monkeypatch.setattr("app.auth.provider_resilience.requests.get", timeout)
    cache = ProviderKeyCache(
        provider="Test",
        fetch_document=lambda: fetch_json_document("https://idp.example.test/keys"),
        key_ids=_certificate_ids,
        policy=_policy,
        clock=lambda: now[0],
    )

    with pytest.raises(ProviderUnavailableError):
        cache.document_for_kid("kid-1")
    with pytest.raises(ProviderUnavailableError):
        cache.document_for_kid("kid-2")
    assert calls == 1

    now[0] += 6
    with pytest.raises(ProviderUnavailableError):
        cache.document_for_kid("kid-1")
    assert calls == 2


def test_unexpected_refresh_failure_releases_single_flight_state():
    now = [100.0]
    calls = 0

    def fetch() -> Mapping[str, Any]:
        nonlocal calls
        calls += 1
        if calls == 1:
            raise RuntimeError("simulated parser regression")
        return {"kid-1": "certificate"}

    cache = ProviderKeyCache(
        provider="Test",
        fetch_document=fetch,
        key_ids=_certificate_ids,
        policy=_policy,
        clock=lambda: now[0],
    )

    with pytest.raises(ProviderUnavailableError):
        cache.document_for_kid("kid-1")
    now[0] += 6
    assert cache.document_for_kid("kid-1") == {"kid-1": "certificate"}
    assert calls == 2


def test_verification_capacity_is_nonblocking_and_recovers():
    gate = NonBlockingCapacity(1, "test verification")

    with gate.slot():
        with pytest.raises(ProviderUnavailableError, match="capacity"):
            with gate.slot():
                raise AssertionError("saturated gate admitted a second verifier")

    with gate.slot():
        pass


def test_saturated_verification_maps_to_bounded_503(monkeypatch):
    gate = NonBlockingCapacity(1, "test verification")
    monkeypatch.setattr(provider_resilience, "_verification_capacity", gate)

    with gate.slot():
        response = TestClient(app).post("/api/auth/google", json={"id_token": "not-reached"})

    assert response.status_code == 503
    assert response.json()["detail"]["code"] == "PROVIDER_UNAVAILABLE"


def test_unknown_google_kid_is_invalid_token_without_network(monkeypatch):
    def unknown(_kid: str) -> Mapping[str, Any]:
        raise ProviderTokenError("unknown signing key id")

    monkeypatch.setattr(google_auth._certificate_cache, "document_for_kid", unknown)
    token = "eyJhbGciOiJSUzI1NiIsImtpZCI6Im5ldmVyLXNlZW4ifQ.e30.signature"
    response = TestClient(app).post("/api/auth/google", json={"id_token": token})

    assert response.status_code == 401
    assert response.json()["detail"]["code"] == "INVALID_TOKEN"


def test_concurrent_key_refresh_is_coalesced():
    refresh_started = threading.Event()
    waiter_started = threading.Event()
    release_refresh = threading.Event()
    calls = 0

    def fetch() -> Mapping[str, Any]:
        nonlocal calls
        calls += 1
        refresh_started.set()
        assert release_refresh.wait(timeout=2)
        return {"kid-1": "certificate"}

    class ObservedCache(ProviderKeyCache):
        def _wait_for_refresh(self, deadline: float) -> None:
            waiter_started.set()
            super()._wait_for_refresh(deadline)

    cache = ObservedCache(
        provider="Test",
        fetch_document=fetch,
        key_ids=_certificate_ids,
        policy=_policy,
    )

    with ThreadPoolExecutor(max_workers=2) as pool:
        first = pool.submit(cache.document_for_kid, "kid-1")
        assert refresh_started.wait(timeout=2)
        second = pool.submit(cache.document_for_kid, "kid-1")
        assert waiter_started.wait(timeout=2)
        release_refresh.set()
        assert first.result(timeout=2) == {"kid-1": "certificate"}
        assert second.result(timeout=2) == {"kid-1": "certificate"}

    assert calls == 1


def test_unknown_kids_are_backed_off_but_real_rotation_refreshes():
    now = [100.0]
    documents: list[Mapping[str, Any]] = [
        {"old-kid": "old-certificate"},
        {"new-kid": "new-certificate"},
    ]
    calls = 0

    def fetch() -> Mapping[str, Any]:
        nonlocal calls
        document = documents[calls]
        calls += 1
        return document

    cache = ProviderKeyCache(
        provider="Test",
        fetch_document=fetch,
        key_ids=_certificate_ids,
        policy=_policy,
        clock=lambda: now[0],
    )

    with pytest.raises(ProviderTokenError, match="unknown"):
        cache.document_for_kid("attacker-kid-1")
    assert cache.document_for_kid("old-kid") == documents[0]
    with pytest.raises(ProviderTokenError, match="unknown"):
        cache.document_for_kid("attacker-kid-2")
    assert calls == 1

    now[0] += 11
    assert cache.document_for_kid("new-kid") == documents[1]
    assert calls == 2


def test_known_stale_key_survives_outage_but_unknown_key_is_503():
    now = [100.0]
    calls = 0

    def fetch() -> Mapping[str, Any]:
        nonlocal calls
        calls += 1
        if calls == 1:
            return {"known-kid": "certificate"}
        raise ProviderUnavailableError("simulated outage")

    cache = ProviderKeyCache(
        provider="Test",
        fetch_document=fetch,
        key_ids=_certificate_ids,
        policy=_policy,
        clock=lambda: now[0],
    )
    first = cache.document_for_kid("known-kid")
    now[0] += 11

    assert cache.document_for_kid("known-kid") == first
    with pytest.raises(ProviderUnavailableError):
        cache.document_for_kid("unknown-during-outage")
    assert calls == 2
