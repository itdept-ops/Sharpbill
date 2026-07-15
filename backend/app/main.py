import logging

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from app.config import settings
from app.errors import install_error_handlers
from app.ratelimit import check as rate_check
from app.request_logging import record_request
from app.routers import auth, dashboard, health, logs, presence, roles, users, ws
from app.routers import settings as settings_router

logging.basicConfig(level=settings.log_level)

# Interactive docs + the OpenAPI schema leak the full route/permission map, so expose them only
# in a local environment; a hosted instance serves neither.
_docs_enabled = settings.app_env == "local"

app = FastAPI(
    title="Kingfisher CRM API",
    docs_url="/api/docs" if _docs_enabled else None,
    openapi_url="/api/openapi.json" if _docs_enabled else None,
)

# If a trusted reverse proxy is configured, let it (and only it) supply the real client IP via
# X-Forwarded-For, so per-IP rate-limiting and the audit log don't collapse to the proxy's IP.
if settings.trusted_proxy_ip_list:
    from uvicorn.middleware.proxy_headers import ProxyHeadersMiddleware

    app.add_middleware(ProxyHeadersMiddleware, trusted_hosts=settings.trusted_proxy_ip_list)

install_error_handlers(app)

# Login-CSRF guard: the cookie-setting login routes reject non-JSON Content-Type. This runs
# before body parsing so a cross-site <form> POST is refused with 415 (SameSite=Lax already
# blocks the cookie on authenticated requests; this closes the cookie-*setting* gap).
# Only the cookie-SETTING routes need this; logout relies on the cookie and is already
# CSRF-safe because SameSite=Lax never sends the session cookie on a cross-site POST.
_JSON_REQUIRED_PATHS = {"/api/auth/google", "/api/auth/microsoft"}


@app.middleware("http")
async def _enforce_json_on_login(request: Request, call_next):
    if request.method == "POST" and request.url.path in _JSON_REQUIRED_PATHS:
        ctype = request.headers.get("content-type", "")
        if not ctype.startswith("application/json"):
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


# Request activity log: records endpoint + user + IP for meaningful requests. Registered after
# the CSRF guard so it is the outermost middleware and observes the final response status.
@app.middleware("http")
async def _request_log(request: Request, call_next):
    try:
        response = await call_next(request)
    except Exception:
        # An unhandled error propagates past here to Starlette's 500 handler; record it as a 500
        # first so error responses are never missing from the audit log, then re-raise.
        try:
            await run_in_threadpool(record_request, request, 500)
        except Exception:
            logging.getLogger("app.requests").exception("request logging error")
        raise
    try:
        # Persistence opens a sync DB session + commits — run it off the event loop so it never
        # blocks request handling or the WebSocket presence loop.
        await run_in_threadpool(record_request, request, response.status_code)
    except Exception:
        logging.getLogger("app.requests").exception("request logging error")
    return response


# Per-IP rate limiting. Registered LAST so it is the OUTERMOST middleware: a throttled request is
# rejected here before it reaches the request log (so a flood can't also flood the audit table) or
# the CPU-heavy token verification. IP is the socket peer (never a spoofable X-Forwarded-For).
_LOGIN_PATHS = {"/api/auth/google", "/api/auth/microsoft", "/api/auth/dev"}
_LOGIN_LIMIT = (20, 60)  # strict: 20 sign-in attempts / minute / IP
_API_LIMIT = (600, 60)  # loose global backstop: 600 requests / minute / IP


@app.middleware("http")
async def _rate_limit(request: Request, call_next):
    ip = request.client.host if request.client else "unknown"
    path = request.url.path
    if path in _LOGIN_PATHS:
        retry = rate_check(f"login:{ip}", *_LOGIN_LIMIT)
    elif path.startswith("/api"):
        retry = rate_check(f"api:{ip}", *_API_LIMIT)
    else:
        retry = 0
    if retry:
        return JSONResponse(
            status_code=429,
            content={
                "detail": {"code": "RATE_LIMITED", "message": "Too many requests — slow down."}
            },
            headers={"Retry-After": str(retry)},
        )
    return await call_next(request)


app.include_router(health.router, prefix="/api")
app.include_router(auth.router, prefix="/api/auth", tags=["auth"])
app.include_router(users.router, prefix="/api/users", tags=["users"])
app.include_router(roles.router, prefix="/api", tags=["rbac"])
app.include_router(presence.router, prefix="/api/presence", tags=["presence"])
app.include_router(settings_router.router, prefix="/api/admin", tags=["settings"])
app.include_router(logs.router, prefix="/api/admin", tags=["logs"])
app.include_router(dashboard.router, prefix="/api", tags=["dashboard"])
app.include_router(ws.router, prefix="/api/ws")

# Dev-only login endpoint — mounted solely in a local environment with the flag on.
if settings.is_dev_auth_enabled:
    from app.routers import dev_auth

    app.include_router(dev_auth.router, prefix="/api/auth", tags=["auth-dev"])
    logging.getLogger("app").warning(
        "DEV AUTH ENABLED: POST /api/auth/dev is active (local only). Do not enable in production."
    )
