import json
import logging
import re
import time
import uuid
from collections import deque
from contextlib import asynccontextmanager
from urllib.parse import urlsplit

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from app.config import settings
from app.errors import install_error_handlers
from app.ratelimit import check as rate_check
from app.request_logging import record_request, shutdown_request_logging, start_request_logging
from app.retention import shutdown_retention_worker, start_retention_worker
from app.routers import auth, dashboard, health, logs, presence, roles, security_events, users, ws
from app.routers import settings as settings_router

logging.basicConfig(level=settings.log_level)
_lifecycle_log = logging.getLogger("app.lifecycle")
_security_log = logging.getLogger("app.security")


def _log_boundary_rejection(scope: Scope, *, status_code: int, code: str) -> None:
    """Emit bounded structured evidence for rejects that never reach request persistence."""
    client = scope.get("client")
    state = scope.get("state") or {}
    _security_log.warning(
        "%s",
        json.dumps(
            {
                "event": "request_boundary_rejected",
                "code": code,
                "status_code": status_code,
                "method": scope.get("method", ""),
                "path": scope.get("path", ""),
                "client_ip": client[0] if client else "unknown",
                "request_id": state.get("request_id"),
            },
            separators=(",", ":"),
        ),
    )


class RequestBodyLimitMiddleware:
    """Buffer and replay at most the configured body budget before request parsing.

    Checking Content-Length alone is insufficient because HTTP/1.1 chunked and HTTP/2 requests
    need not carry it. Pre-reading at the ASGI boundary keeps memory bounded for both forms and
    ensures an oversized unauthenticated token never reaches Pydantic, a provider, or the DB.
    """

    def __init__(self, app: ASGIApp, max_bytes: int) -> None:
        self.app = app
        self.max_bytes = max_bytes

    async def _reject(self, scope: Scope, receive: Receive, send: Send) -> None:
        _log_boundary_rejection(scope, status_code=413, code="REQUEST_TOO_LARGE")
        response = JSONResponse(
            status_code=413,
            content={
                "detail": {
                    "code": "REQUEST_TOO_LARGE",
                    "message": "Request body exceeds the allowed size",
                }
            },
        )
        await response(scope, receive, send)

    def _declared_body_is_oversized(self, scope: Scope) -> bool:
        headers = {key.lower(): value for key, value in scope.get("headers", [])}
        raw_length = headers.get(b"content-length")
        if raw_length is None:
            return False
        try:
            return int(raw_length) > self.max_bytes
        except ValueError:
            # Uvicorn normally rejects malformed Content-Length before ASGI. If another server
            # forwards it, the streamed-byte check remains authoritative.
            return False

    async def _buffer_body(self, receive: Receive) -> tuple[deque[Message], bool]:
        buffered: deque[Message] = deque()
        total = 0
        while True:
            message = await receive()
            buffered.append(message)
            if message["type"] == "http.disconnect":
                return buffered, False
            if message["type"] != "http.request":
                continue
            total += len(message.get("body", b""))
            if total > self.max_bytes:
                return buffered, True
            if not message.get("more_body", False):
                return buffered, False

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return

        if self._declared_body_is_oversized(scope):
            await self._reject(scope, receive, send)
            return

        buffered, oversized = await self._buffer_body(receive)
        if oversized:
            await self._reject(scope, receive, send)
            return

        async def replay() -> Message:
            return buffered.popleft() if buffered else await receive()

        await self.app(scope, replay, send)


# Interactive docs + the OpenAPI schema leak the full route/permission map, so expose them only
# in a local environment; a hosted instance serves neither.
_docs_enabled = settings.app_env == "local"


@asynccontextmanager
async def _lifespan(_: FastAPI):
    start_request_logging()
    start_retention_worker()
    try:
        yield
    finally:
        failed_components: list[str] = []
        try:
            if not shutdown_retention_worker():
                failed_components.append("database_retention")
        except Exception:
            failed_components.append("database_retention")
            _lifecycle_log.exception(
                "%s",
                json.dumps(
                    {
                        "event": "background_shutdown_exception",
                        "component": "database_retention",
                    },
                    separators=(",", ":"),
                ),
            )
        try:
            if not shutdown_request_logging():
                failed_components.append("request_log_writer")
        except Exception:
            failed_components.append("request_log_writer")
            _lifecycle_log.exception(
                "%s",
                json.dumps(
                    {
                        "event": "background_shutdown_exception",
                        "component": "request_log_writer",
                    },
                    separators=(",", ":"),
                ),
            )
        if failed_components:
            _lifecycle_log.critical(
                "%s",
                json.dumps(
                    {
                        "event": "background_shutdown_incomplete",
                        "components": failed_components,
                    },
                    separators=(",", ":"),
                ),
            )
            # Both workers use bounded joins and daemon threads, so this reaches supervision
            # promptly. Raising makes Uvicorn report lifespan shutdown failure instead of
            # presenting a partial drain as a clean stop.
            raise RuntimeError("background worker shutdown was incomplete")


app = FastAPI(
    title="Kingfisher CRM API",
    docs_url="/api/docs" if _docs_enabled else None,
    openapi_url="/api/openapi.json" if _docs_enabled else None,
    lifespan=_lifespan,
)

install_error_handlers(app)

# Cookie-setting login routes also require JSON. The general unsafe-method Origin/Fetch Metadata
# policy below protects every mutation, including against sibling origins that are still same-site.
_JSON_REQUIRED_PATHS = {"/api/auth/google", "/api/auth/microsoft", "/api/auth/dev"}


@app.middleware("http")
async def _enforce_json_on_login(request: Request, call_next):
    if request.method == "POST" and request.url.path in _JSON_REQUIRED_PATHS:
        ctype = request.headers.get("content-type", "")
        if not ctype.startswith("application/json"):
            _log_boundary_rejection(request.scope, status_code=415, code="UNSUPPORTED_MEDIA_TYPE")
            return JSONResponse(
                status_code=415,
                content={
                    "detail": {
                        "code": "UNSUPPORTED_MEDIA_TYPE",
                        "message": "Content-Type must be application/json",
                    }
                },
            )
    return await call_next(request)


# Request activity log: records endpoint + user + IP for meaningful requests.
@app.middleware("http")
async def _request_log(request: Request, call_next):
    try:
        response = await call_next(request)
    except Exception:
        # An unhandled error propagates past here to Starlette's 500 handler; record it as a 500
        # first so error responses are never missing from the audit log, then re-raise.
        try:
            record_request(request, 500)
        except Exception:
            logging.getLogger("app.requests").exception("request logging error")
        raise
    try:
        # Structured stdout plus a non-blocking bounded enqueue; the writer owns DB latency.
        record_request(request, response.status_code)
    except Exception:
        logging.getLogger("app.requests").exception("request logging error")
    return response


# Insert the body boundary outside request persistence but inside the later rate/correlation
# middleware. Oversized unauthenticated traffic therefore gets a correlated 413 without touching
# provider code or amplifying into a database audit write.
app.add_middleware(RequestBodyLimitMiddleware, max_bytes=settings.request_body_max_bytes)


# Per-IP rate limiting rejects traffic before request-log persistence or token verification. Trusted
# proxy resolution is registered outside it below, so its IP is either the verified XFF client or
# the untrusted request's socket peer.
_LOGIN_PATHS = {"/api/auth/google", "/api/auth/microsoft", "/api/auth/dev"}
_NONCE_PATH = "/api/auth/nonce"
_RATE_LIMIT_EXEMPT_PATHS = {"/api/health/live"}
_READINESS_PATHS = {"/api/health", "/api/health/ready"}
_LOGIN_LIMIT = (20, 60)  # strict: 20 sign-in attempts / minute / IP
_NONCE_LIMIT = (30, 60)  # nonce issuance is an unauthenticated DB write
_READINESS_LIMIT = (30, 60)  # enough for probes, bounded against public DB-pool exhaustion
_API_LIMIT = (600, 60)  # loose global backstop: 600 requests / minute / IP


@app.middleware("http")
async def _rate_limit(request: Request, call_next):
    ip = request.client.host if request.client else "unknown"
    path = request.url.path
    if path in _RATE_LIMIT_EXEMPT_PATHS:
        # Process-only liveness never touches dependencies and must remain available to supervision.
        retry = 0
    elif path in _READINESS_PATHS:
        # Readiness performs bounded database checks. Give probes an independent bucket so normal
        # API traffic cannot starve them, while public callers cannot use them as a DB-pool bypass.
        retry = rate_check(f"readiness:{ip}", *_READINESS_LIMIT)
    elif path == _NONCE_PATH:
        retry = rate_check(f"nonce:{ip}", *_NONCE_LIMIT)
    elif path in _LOGIN_PATHS:
        retry = rate_check(f"login:{ip}", *_LOGIN_LIMIT)
    elif path.startswith("/api"):
        retry = rate_check(f"api:{ip}", *_API_LIMIT)
    else:
        retry = 0
    if retry:
        _log_boundary_rejection(request.scope, status_code=429, code="RATE_LIMITED")
        return JSONResponse(
            status_code=429,
            content={
                "detail": {"code": "RATE_LIMITED", "message": "Too many requests — slow down."}
            },
            headers={"Retry-After": str(retry)},
        )
    return await call_next(request)


_UNSAFE_METHODS = {"POST", "PUT", "PATCH", "DELETE"}
_PROVENANCE_OPTIONAL_PATHS = {*_LOGIN_PATHS, _NONCE_PATH}
_REQUEST_ID_RE = re.compile(r"^[A-Za-z0-9._-]{1,64}$")


def _origin_tuple(value: str) -> tuple[str, str, int] | None:
    """Normalize an HTTP(S) origin, including explicit/default ports."""
    try:
        parsed = urlsplit(value)
        if parsed.scheme not in {"http", "https"} or not parsed.hostname:
            return None
        default_port = 443 if parsed.scheme == "https" else 80
        return parsed.scheme, parsed.hostname.lower(), parsed.port or default_port
    except ValueError:
        return None


def _same_origin(request: Request, candidate: str) -> bool:
    if settings.app_env == "production":
        expected = _origin_tuple(settings.public_origin)
    else:
        host = request.headers.get("host", "")
        expected = _origin_tuple(f"{request.url.scheme}://{host}")
    return expected is not None and _origin_tuple(candidate) == expected


def _csrf_rejected(request: Request) -> bool:
    if request.method not in _UNSAFE_METHODS or not request.url.path.startswith("/api"):
        return False
    fetch_site = request.headers.get("sec-fetch-site", "").lower()
    if fetch_site in {"cross-site", "same-site"}:
        return True
    origin = request.headers.get("origin")
    if origin is not None:
        return not _same_origin(request, origin)
    referer = request.headers.get("referer")
    if referer is not None:
        return not _same_origin(request, referer)
    if fetch_site in {"same-origin", "none"}:
        return False
    # Non-browser API clients, nonce issuance, and sign-in calls remain compatible without browser
    # provenance. Once a production session cookie is present, however, an unsafe authenticated
    # mutation with no trusted provenance is indistinguishable from a stripped-header CSRF attempt.
    if (
        settings.app_env == "production"
        and request.url.path not in _PROVENANCE_OPTIONAL_PATHS
        and request.cookies.get(settings.session_cookie_name) is not None
    ):
        return True
    return False


@app.middleware("http")
async def _security_and_correlation(request: Request, call_next):
    supplied_id = request.headers.get("x-request-id", "")
    request_id = supplied_id if _REQUEST_ID_RE.fullmatch(supplied_id) else uuid.uuid4().hex
    request.state.request_id = request_id
    request.state.started_at = time.perf_counter()

    if _csrf_rejected(request):
        _log_boundary_rejection(request.scope, status_code=403, code="CSRF_REJECTED")
        response = JSONResponse(
            status_code=403,
            content={
                "detail": {
                    "code": "CSRF_REJECTED",
                    "message": "Cross-origin state-changing requests are not allowed",
                }
            },
        )
    else:
        response = await call_next(request)

    request.state.duration_ms = (time.perf_counter() - request.state.started_at) * 1000
    response.headers["X-Request-ID"] = request_id
    if request.url.path.startswith("/api"):
        response.headers["Cache-Control"] = "no-store, max-age=0"
        response.headers["Pragma"] = "no-cache"
    return response


# Register trusted proxy handling LAST so it is the outermost ASGI middleware. The socket peer is
# checked against the explicit trust list before X-Forwarded-For rewrites request.client, and the
# rewritten client is then visible to CSRF URL handling, rate limiting, sessions, and audit logs.
if settings.trusted_proxy_ip_list:
    from uvicorn.middleware.proxy_headers import ProxyHeadersMiddleware

    app.add_middleware(ProxyHeadersMiddleware, trusted_hosts=settings.trusted_proxy_ip_list)


app.include_router(health.router, prefix="/api")
app.include_router(auth.router, prefix="/api/auth", tags=["auth"])
app.include_router(users.router, prefix="/api/users", tags=["users"])
app.include_router(roles.router, prefix="/api", tags=["rbac"])
app.include_router(presence.router, prefix="/api/presence", tags=["presence"])
app.include_router(settings_router.router, prefix="/api/admin", tags=["settings"])
app.include_router(logs.router, prefix="/api/admin", tags=["logs"])
app.include_router(security_events.router, prefix="/api/admin", tags=["security-events"])
app.include_router(dashboard.router, prefix="/api", tags=["dashboard"])
app.include_router(ws.router, prefix="/api/ws")

# Dev-only login endpoint — mounted solely in a local environment with the flag on.
if settings.is_dev_auth_enabled:
    from app.routers import dev_auth

    app.include_router(dev_auth.router, prefix="/api/auth", tags=["auth-dev"])
    logging.getLogger("app").warning(
        "DEV AUTH ENABLED: POST /api/auth/dev is active with shared-secret protection (local only)."
    )
elif settings.app_env == "local" and settings.dev_auth_enabled:
    logging.getLogger("app").warning(
        "DEV_AUTH_ENABLED was requested but the route is disabled: configure a separate strong "
        "DEV_AUTH_SECRET."
    )
