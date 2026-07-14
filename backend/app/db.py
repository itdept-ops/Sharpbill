from collections.abc import Iterator

from sqlalchemy import create_engine
from sqlalchemy.orm import Session, sessionmaker

from app.config import settings

_connect_args: dict = {}
if settings.db_require_tls:
    # RDS certs chain to Amazon's private RDS CAs, not the system trust store.
    # The Dockerfile downloads this bundle.
    _connect_args["ssl"] = {"ca": "/app/rds-global-bundle.pem"}

engine = create_engine(
    settings.database_url,
    pool_pre_ping=True,
    pool_recycle=280,  # below MySQL/RDS wait_timeout and NAT idle limits
    connect_args=_connect_args,
)
SessionLocal = sessionmaker(bind=engine, autoflush=False, expire_on_commit=False)


def get_db() -> Iterator[Session]:
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
