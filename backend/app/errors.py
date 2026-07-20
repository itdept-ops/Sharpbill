"""Uniform error shape: every non-2xx body is {"detail": {"code", "message"}}.

Raising HTTPException(status, {"code": ..., "message": ...}) yields that shape directly;
these handlers keep validation and unexpected errors consistent with it.
"""

import logging
from collections.abc import Mapping

from fastapi import FastAPI, HTTPException, Request
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool
from starlette.exceptions import HTTPException as StarletteHTTPException

log = logging.getLogger("app")

# Validation errors can carry the raw request body (bytes) in `input`, which is not
# JSON-serializable; decode it so the error handler never 500s while reporting a 422.
_ENCODERS = {bytes: lambda b: b.decode("utf-8", "replace")}
_UNSAFE_METHODS = frozenset({"POST", "PUT", "PATCH", "DELETE"})
_AUDITED_DENIAL_STATUSES = frozenset({403, 409, 428})


class ApiError(HTTPException):
    def __init__(
        self,
        status_code: int,
        code: str,
        message: str,
        *,
        headers: dict[str, str] | None = None,
    ):
        self.code = code
        super().__init__(
            status_code=status_code,
            detail={"code": code, "message": message},
            headers=headers,
        )


def _record_privileged_denial(
    request: Request, *, status_code: int, detail: Mapping[str, object]
) -> None:
    """Persist sanitized evidence independently of the refused business transaction."""
    actor_user_id = getattr(request.state, "user_id", None)
    if (
        request.method not in _UNSAFE_METHODS
        or status_code not in _AUDITED_DENIAL_STATUSES
        or not isinstance(actor_user_id, int)
    ):
        return

    # Import lazily so the foundational errors module stays free of an import cycle with DB/auth.
    from app.db import SessionLocal
    from app.security_events import commit_security_event

    route = request.scope.get("route")
    route_path = getattr(route, "path", None) or request.url.path
    db = SessionLocal()
    try:
        commit_security_event(
            db,
            event_type="privileged_mutation.denied",
            outcome="denied",
            severity="warning",
            request=request,
            actor_user_id=actor_user_id,
            target_type="api_route",
            target_id=str(route_path),
            metadata={
                "method": request.method,
                "status_code": status_code,
                "code": str(detail.get("code", "HTTP_ERROR"))[:80],
            },
        )
    except Exception:
        db.rollback()
        # Evidence persistence must fail open for the original deterministic error response.
        log.exception("Unable to persist denied-mutation security evidence")
    finally:
        db.close()


def install_error_handlers(app: FastAPI) -> None:
    @app.exception_handler(StarletteHTTPException)
    async def _http_error(request: Request, exc: StarletteHTTPException):
        if isinstance(exc.detail, dict) and {"code", "message"} <= exc.detail.keys():
            detail = exc.detail
        else:
            code = {
                400: "BAD_REQUEST",
                401: "NOT_AUTHENTICATED",
                403: "FORBIDDEN",
                404: "NOT_FOUND",
                405: "METHOD_NOT_ALLOWED",
                409: "CONFLICT",
                413: "CONTENT_TOO_LARGE",
                415: "UNSUPPORTED_MEDIA_TYPE",
                428: "PRECONDITION_REQUIRED",
                429: "RATE_LIMITED",
            }.get(exc.status_code, "HTTP_ERROR")
            detail = {"code": code, "message": str(exc.detail)}
        await run_in_threadpool(
            _record_privileged_denial,
            request,
            status_code=exc.status_code,
            detail=detail,
        )
        return JSONResponse(
            status_code=exc.status_code,
            content={"detail": detail},
            headers=exc.headers,
        )

    @app.exception_handler(RequestValidationError)
    async def _validation(request: Request, exc: RequestValidationError):
        # Return field-level errors but strip `input` (the raw submitted value) and `url` so we
        # never reflect the caller's payload or internal doc links back.
        errors = [
            {k: v for k, v in err.items() if k not in ("input", "url")} for err in exc.errors()
        ]
        return JSONResponse(
            status_code=422,
            content={
                "detail": {
                    "code": "VALIDATION_ERROR",
                    "message": "Invalid request",
                    "errors": jsonable_encoder(errors, custom_encoder=_ENCODERS),
                }
            },
        )

    @app.exception_handler(Exception)
    async def _unhandled(request: Request, exc: Exception):
        log.exception("Unhandled error on %s %s", request.method, request.url.path)
        return JSONResponse(
            status_code=500,
            content={"detail": {"code": "INTERNAL_ERROR", "message": "Internal server error"}},
        )
