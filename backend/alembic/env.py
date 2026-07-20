import re
from logging.config import fileConfig
from typing import Any

from sqlalchemy import create_engine, pool

from alembic import context
from app.config import settings
from app.db import mysql_migration_connect_args
from app.models import Base  # imports all models as a side effect

config = context.config
# Escape percent signs so ConfigParser doesn't choke on URL-encoded passwords.
config.set_main_option("sqlalchemy.url", settings.database_url.replace("%", "%%"))

if config.config_file_name is not None:
    fileConfig(config.config_file_name)

target_metadata = Base.metadata

_MYSQL_TIMESTAMP = re.compile(
    r"\b(?:current_timestamp|now)\s*(?:\(\s*(\d*)\s*\))?",
    flags=re.IGNORECASE,
)


def _normalize_mysql_timestamp_default(value: Any) -> str | None:
    """Canonicalize MySQL's equivalent CURRENT_TIMESTAMP/now spellings."""
    if value is None:
        return None
    normalized = str(value).strip().lower().replace("`", "")
    normalized = _MYSQL_TIMESTAMP.sub(
        lambda match: f"current_timestamp({match.group(1)})"
        if match.group(1)
        else "current_timestamp",
        normalized,
    )
    normalized = re.sub(r"\s+", " ", normalized)
    # MySQL reflection and SQLAlchemy rendering may add one harmless outer pair.
    while normalized.startswith("(") and normalized.endswith(")"):
        depth = 0
        wraps_entire_expression = True
        for index, character in enumerate(normalized):
            if character == "(":
                depth += 1
            elif character == ")":
                depth -= 1
                if depth == 0 and index != len(normalized) - 1:
                    wraps_entire_expression = False
                    break
        if not wraps_entire_expression:
            break
        normalized = normalized[1:-1].strip()
    return normalized


def _compare_server_default(
    migration_context: Any,
    inspected_column: Any,
    metadata_column: Any,
    inspected_default: Any,
    metadata_default: Any,
    rendered_metadata_default: Any,
) -> bool | None:
    """Suppress only MySQL timestamp-default spelling noise; compare everything else normally."""
    del inspected_column, metadata_column, metadata_default
    if migration_context.dialect.name != "mysql":
        return None
    inspected = _normalize_mysql_timestamp_default(inspected_default)
    rendered = _normalize_mysql_timestamp_default(rendered_metadata_default)
    if inspected and rendered and "current_timestamp" in inspected and inspected == rendered:
        return False
    return None


def run_migrations_offline() -> None:
    context.configure(
        url=config.get_main_option("sqlalchemy.url"),
        target_metadata=target_metadata,
        literal_binds=True,
        compare_type=True,
        compare_server_default=_compare_server_default,
    )
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    connectable = create_engine(
        settings.database_url,
        poolclass=pool.NullPool,
        pool_pre_ping=True,
        connect_args=mysql_migration_connect_args(settings),
    )
    with connectable.connect() as connection:
        context.configure(
            connection=connection,
            target_metadata=target_metadata,
            compare_type=True,
            compare_server_default=_compare_server_default,
        )
        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
