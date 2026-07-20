from collections.abc import Iterator
from typing import Any

from sqlalchemy import create_engine
from sqlalchemy.orm import Session, sessionmaker

from app.config import Settings, settings


def mysql_connect_args(config: Settings) -> dict[str, Any]:
    """Return the verified TLS, UTC, and driver-timeout policy for every DB client."""
    connect_args: dict[str, Any] = {
        # Pin every connection's session time zone to UTC so DB-generated timestamps
        # and app-generated UTC-naive datetimes share one frame.
        "init_command": "SET time_zone = '+00:00'",
        "connect_timeout": config.db_connect_timeout_seconds,
        "read_timeout": config.db_read_timeout_seconds,
        "write_timeout": config.db_write_timeout_seconds,
    }
    if config.db_require_tls:
        # A configured CA makes PyMySQL use CERT_REQUIRED; hostname checking additionally
        # rejects a valid certificate issued for a different database endpoint.
        connect_args["ssl"] = {
            "ca": config.db_tls_ca_path,
            "check_hostname": True,
        }
    return connect_args


def mysql_migration_connect_args(config: Settings) -> dict[str, Any]:
    """Keep verified TLS/UTC/connect policy without aborting long, auto-committing DDL.

    Runtime read/write socket deadlines protect request capacity. Schema changes have separate
    operator time budgets and metadata-lock preflights; disconnecting a migration client after a
    short runtime deadline can leave MySQL finishing DDL while Alembic records no revision.
    """
    connect_args = mysql_connect_args(config)
    connect_args.pop("read_timeout", None)
    connect_args.pop("write_timeout", None)
    return connect_args


def runtime_engine_options(config: Settings) -> dict[str, Any]:
    """Build the bounded runtime-pool options from validated application settings."""
    return {
        "pool_pre_ping": True,
        "pool_size": config.db_pool_size,
        "max_overflow": config.db_max_overflow,
        "pool_timeout": config.db_pool_timeout_seconds,
        "pool_recycle": config.db_pool_recycle_seconds,
        "connect_args": mysql_connect_args(config),
    }


engine = create_engine(
    settings.database_url,
    **runtime_engine_options(settings),
)
SessionLocal = sessionmaker(bind=engine, autoflush=False, expire_on_commit=False)


def get_db() -> Iterator[Session]:
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
