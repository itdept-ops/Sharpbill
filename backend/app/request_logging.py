import logging
from datetime import UTC, datetime, timedelta

from fastapi import Request
from sqlalchemy import delete

from app.auth.jwt import COOKIE_NAME, decode_session_token
from app.db import SessionLocal
from app.models import RequestLog

_log = logging.getLogger("app.requests")

# Retention: keep the audit log bounded without a separate cron. Every _PRUNE_EVERY inserts we
# delete rows older than _RETENTION_DAYS. Approximate (the counter isn't lock-guarded) but enough
# to stop unbounded growth of an append-on-every-request table.
_RETENTION_DAYS = 90
_PRUNE_EVERY = 500
_insert_counter = 0

# Frequent/noisy paths we don't persist (health checks, the WS, docs, polling, session probes).
_SKIP_PREFIXES = (
    "/api/health",
    "/api/ws",
    "/api/docs",
    "/api/openapi",
    "/api/presence",
    "/api/auth/config",
    "/api/auth/me",
)


def _should_log(method: str, path: str) -> bool:
    if method == "OPTIONS" or not path.startswith("/api"):
        return False
    return not any(path.startswith(p) for p in _SKIP_PREFIXES)


def _client_ip(request: Request) -> str | None:
    # Use the socket peer, not a caller-supplied X-Forwarded-For (which any direct client can
    # spoof to forge the logged source IP). When a trusted reverse proxy / ALB is added, mount
    # Uvicorn/Starlette proxy-headers middleware with an explicit trusted-hosts list — that
    # rewrites request.client.host from XFF safely, and this stays correct.
    return request.client.host if request.client else None


def _user_id(request: Request) -> int | None:
    # Reuse the principal already resolved by get_current_user (avoids a second JWT decode);
    # fall back to decoding the cookie for requests that never hit the auth dependency.
    uid = getattr(request.state, "user_id", None)
    if uid is not None:
        return uid
    token = request.cookies.get(COOKIE_NAME)
    if not token:
        return None
    try:
        return int(decode_session_token(token)["sub"])
    except Exception:
        return None


def prune_request_logs(db, older_than_days: int = _RETENTION_DAYS) -> int:
    """Delete audit rows older than the retention window. Returns the number removed."""
    cutoff = datetime.now(UTC).replace(tzinfo=None) - timedelta(days=older_than_days)
    result = db.execute(delete(RequestLog).where(RequestLog.created_at < cutoff))
    db.commit()
    return result.rowcount or 0


def record_request(request: Request, status_code: int) -> None:
    """Log endpoint + user + IP for a request, and persist a row for meaningful ones."""
    global _insert_counter
    method, path = request.method, request.url.path
    if not _should_log(method, path):
        return
    ip = _client_ip(request)
    uid = _user_id(request)
    _log.info("%s %s user=%s ip=%s -> %s", method, path, uid, ip, status_code)
    try:
        with SessionLocal() as db:
            db.add(
                RequestLog(
                    method=method[:10],
                    path=path[:255],
                    user_id=uid,
                    ip=(ip[:45] if ip else None),
                    status_code=status_code,
                )
            )
            db.commit()
            _insert_counter += 1
            if _insert_counter % _PRUNE_EVERY == 0:  # periodic retention sweep
                prune_request_logs(db)
    except Exception:  # never let logging break a request
        _log.exception("failed to persist request log")
