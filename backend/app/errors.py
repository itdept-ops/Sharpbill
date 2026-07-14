"""Uniform error shape: every non-2xx body is {"detail": {"code", "message"}}.

Raising HTTPException(status, {"code": ..., "message": ...}) yields that shape directly;
these handlers keep validation and unexpected errors consistent with it.
"""

import logging

from fastapi import FastAPI, HTTPException, Request
from fastapi.encoders import jsonable_encoder
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

log = logging.getLogger("app")

# Validation errors can carry the raw request body (bytes) in `input`, which is not
# JSON-serializable; decode it so the error handler never 500s while reporting a 422.
_ENCODERS = {bytes: lambda b: b.decode("utf-8", "replace")}


class ApiError(HTTPException):
    def __init__(self, status_code: int, code: str, message: str):
        super().__init__(status_code=status_code, detail={"code": code, "message": message})


def install_error_handlers(app: FastAPI) -> None:
    @app.exception_handler(RequestValidationError)
    async def _validation(request: Request, exc: RequestValidationError):
        return JSONResponse(
            status_code=422,
            content={
                "detail": {
                    "code": "VALIDATION_ERROR",
                    "message": "Invalid request",
                    "errors": jsonable_encoder(exc.errors(), custom_encoder=_ENCODERS),
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
