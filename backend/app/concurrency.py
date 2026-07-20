from app.errors import ApiError


def require_version(expected: int | None, current: int, resource: str) -> None:
    """Enforce an optimistic-concurrency precondition after authorization and row locking."""
    if expected is None:
        raise ApiError(
            428,
            "PRECONDITION_REQUIRED",
            f"{resource} updates require the version returned by the latest read",
        )
    if expected != current:
        raise ApiError(
            409,
            "STALE_WRITE",
            f"{resource} changed since it was loaded; refresh and retry",
        )
