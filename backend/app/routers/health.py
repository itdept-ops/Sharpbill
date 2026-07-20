import json
import threading
import time
from functools import lru_cache
from pathlib import Path
from typing import cast

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse
from sqlalchemy import text
from sqlalchemy.orm import Session

from alembic.config import Config
from alembic.script import ScriptDirectory
from app.admin_access import administration_available
from app.config import settings
from app.db import get_db
from app.models import SiteSettings

router = APIRouter()

_READINESS_CACHE_TTL_SECONDS = 2.0
_readiness_probe_lock = threading.Lock()
_readiness_cache: tuple[float, int, dict[str, str]] | None = None


def _provider_state(site: SiteSettings | None) -> tuple[bool, bool, bool]:
    google = bool(site and settings.google_provider_configured and site.allow_google)
    microsoft = bool(site and settings.microsoft_provider_configured and site.allow_microsoft)
    return google, microsoft, settings.is_dev_auth_enabled


@lru_cache(maxsize=1)
def _expected_schema_heads() -> frozenset[str]:
    """Read the schema contract from the migrations packaged with this application image."""
    migrations = Path(__file__).resolve().parents[2] / "alembic"
    config = Config()
    config.set_main_option("script_location", str(migrations))
    return frozenset(ScriptDirectory.from_config(config).get_heads())


def _uncached_readiness(db: Session) -> JSONResponse:
    """Check database, schema, and at least one effective identity path without leaking details."""
    try:
        db.execute(text("SELECT 1"))
        actual_heads = frozenset(
            db.execute(text("SELECT version_num FROM alembic_version")).scalars()
        )
        site = db.get(SiteSettings, 1)
        google, microsoft, dev = _provider_state(site)
        admin_available = administration_available(db, google=google, microsoft=microsoft, dev=dev)
        unsafe_default = bool(
            db.scalar(
                text(
                    "SELECT EXISTS(SELECT 1 FROM site_settings s "
                    "JOIN roles r ON r.id=s.default_role_id WHERE r.name='admin')"
                )
            )
        )
    except Exception:
        return JSONResponse(
            status_code=503,
            content={
                "status": "not_ready",
                "database": "error",
                "schema": "unknown",
                "identity_provider": "unknown",
                "administration": "unknown",
                "admission_policy": "unknown",
            },
        )
    if actual_heads != _expected_schema_heads():
        return JSONResponse(
            status_code=503,
            content={
                "status": "not_ready",
                "database": "ok",
                "schema": "mismatch",
                "identity_provider": "unknown",
                "administration": "unknown",
                "admission_policy": "unknown",
            },
        )
    provider_available = google or microsoft or dev
    if not provider_available:
        return JSONResponse(
            status_code=503,
            content={
                "status": "not_ready",
                "database": "ok",
                "schema": "ok",
                "identity_provider": "unavailable",
                "administration": "unknown",
                "admission_policy": "unsafe" if unsafe_default else "ok",
            },
        )
    if unsafe_default:
        return JSONResponse(
            status_code=503,
            content={
                "status": "not_ready",
                "database": "ok",
                "schema": "ok",
                "identity_provider": "ok",
                "administration": "unknown",
                "admission_policy": "unsafe",
            },
        )
    if not admin_available:
        return JSONResponse(
            status_code=503,
            content={
                "status": "not_ready",
                "database": "ok",
                "schema": "ok",
                "identity_provider": "ok",
                "administration": "unavailable",
                "admission_policy": "ok",
            },
        )
    return JSONResponse(
        status_code=200,
        content={
            "status": "ready",
            "database": "ok",
            "schema": "ok",
            "identity_provider": "ok",
            "administration": "ok",
            "admission_policy": "ok",
        },
    )


def _busy_readiness() -> JSONResponse:
    """Fail closed instead of queueing worker threads behind an in-flight database probe."""
    return JSONResponse(
        status_code=503,
        content={
            "status": "not_ready",
            "database": "probe_in_progress",
            "schema": "unknown",
            "identity_provider": "unknown",
            "administration": "unknown",
            "admission_policy": "unknown",
        },
    )


def _readiness(db: Session) -> JSONResponse:
    """Return a brief cached snapshot and coalesce concurrent database probes."""
    global _readiness_cache

    now = time.monotonic()
    cached = _readiness_cache
    if cached is not None and cached[0] > now:
        return JSONResponse(status_code=cached[1], content=dict(cached[2]))

    if not _readiness_probe_lock.acquire(blocking=False):
        return _busy_readiness()
    try:
        # A probe could have populated the cache between the optimistic read and lock acquisition.
        now = time.monotonic()
        cached = _readiness_cache
        if cached is not None and cached[0] > now:
            return JSONResponse(status_code=cached[1], content=dict(cached[2]))

        response = _uncached_readiness(db)
        content = cast(dict[str, str], json.loads(bytes(response.body)))
        _readiness_cache = (
            time.monotonic() + _READINESS_CACHE_TTL_SECONDS,
            response.status_code,
            content,
        )
        return JSONResponse(status_code=response.status_code, content=dict(content))
    finally:
        _readiness_probe_lock.release()


def _reset_readiness_cache() -> None:
    """Clear the process-local snapshot between tests and after explicit control-plane changes."""
    global _readiness_cache

    with _readiness_probe_lock:
        _readiness_cache = None


@router.get("/health/live", include_in_schema=False)
def liveness() -> dict[str, str]:
    """Process-only probe: dependencies never turn a live process into a restart loop."""
    return {"status": "alive"}


@router.get("/health/ready", include_in_schema=False)
def readiness(db: Session = Depends(get_db)) -> JSONResponse:
    """Traffic admission requires MySQL, the schema head, and a usable login provider."""
    return _readiness(db)


@router.get("/health")
def health(db: Session = Depends(get_db)) -> JSONResponse:
    """Backward-compatible readiness alias used by existing local tooling."""
    return _readiness(db)
