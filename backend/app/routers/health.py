from functools import lru_cache
from pathlib import Path

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse
from sqlalchemy import text
from sqlalchemy.orm import Session

from alembic.config import Config
from alembic.script import ScriptDirectory
from app.db import get_db

router = APIRouter()


@lru_cache(maxsize=1)
def _expected_schema_heads() -> frozenset[str]:
    """Read the schema contract from the migrations packaged with this application image."""
    migrations = Path(__file__).resolve().parents[2] / "alembic"
    config = Config()
    config.set_main_option("script_location", str(migrations))
    return frozenset(ScriptDirectory.from_config(config).get_heads())


def _readiness(db: Session) -> JSONResponse:
    """Check both database connectivity and exact Alembic revision without leaking details."""
    try:
        db.execute(text("SELECT 1"))
        actual_heads = frozenset(
            db.execute(text("SELECT version_num FROM alembic_version")).scalars()
        )
    except Exception:
        return JSONResponse(
            status_code=503,
            content={"status": "not_ready", "database": "error", "schema": "unknown"},
        )
    if actual_heads != _expected_schema_heads():
        return JSONResponse(
            status_code=503,
            content={"status": "not_ready", "database": "ok", "schema": "mismatch"},
        )
    return JSONResponse(
        status_code=200,
        content={"status": "ready", "database": "ok", "schema": "ok"},
    )


@router.get("/health/live", include_in_schema=False)
def liveness() -> dict[str, str]:
    """Process-only probe: dependencies never turn a live process into a restart loop."""
    return {"status": "alive"}


@router.get("/health/ready", include_in_schema=False)
def readiness(db: Session = Depends(get_db)) -> JSONResponse:
    """Traffic-admission probe: require MySQL and the packaged schema head."""
    return _readiness(db)


@router.get("/health")
def health(db: Session = Depends(get_db)) -> JSONResponse:
    """Backward-compatible readiness alias used by existing local tooling."""
    return _readiness(db)
