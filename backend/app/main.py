import logging

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from app.config import settings
from app.errors import install_error_handlers
from app.request_logging import record_request
from app.routers import auth, dashboard, health, logs, presence, roles, users, ws
from app.routers import settings as settings_router

logging.basicConfig(level=settings.log_level)

app = FastAPI(
    title="Kingfisher CRM API",
    docs_url="/api/docs",
    openapi_url="/api/openapi.json",
)

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
    response = await call_next(request)
    try:
        record_request(request, response.status_code)
    except Exception:
        logging.getLogger("app.requests").exception("request logging error")
    return response


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
