"""Application lifespan reports incomplete bounded background-worker shutdowns."""

import asyncio
import json

import pytest

from app import main as main_module


def test_lifespan_reports_all_incomplete_background_shutdowns(monkeypatch):
    shutdown_calls: list[str] = []
    critical_payloads: list[str] = []
    monkeypatch.setattr(main_module, "start_request_logging", lambda: None)
    monkeypatch.setattr(main_module, "start_retention_worker", lambda: None)

    def retention_shutdown() -> bool:
        shutdown_calls.append("database_retention")
        return False

    def request_log_shutdown() -> bool:
        shutdown_calls.append("request_log_writer")
        return False

    monkeypatch.setattr(main_module, "shutdown_retention_worker", retention_shutdown)
    monkeypatch.setattr(main_module, "shutdown_request_logging", request_log_shutdown)
    monkeypatch.setattr(
        main_module._lifecycle_log,
        "critical",
        lambda _template, payload: critical_payloads.append(payload),
    )

    async def exercise() -> None:
        with pytest.raises(RuntimeError, match="background worker shutdown was incomplete"):
            async with main_module._lifespan(main_module.app):
                pass

    asyncio.run(exercise())

    assert shutdown_calls == ["database_retention", "request_log_writer"]
    assert len(critical_payloads) == 1
    assert json.loads(critical_payloads[0]) == {
        "event": "background_shutdown_incomplete",
        "components": ["database_retention", "request_log_writer"],
    }
