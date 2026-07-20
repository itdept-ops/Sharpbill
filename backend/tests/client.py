"""Test HTTP client that explicitly authenticates to the local-only dev-login seam."""

from collections.abc import Mapping

from fastapi.testclient import TestClient as FastAPITestClient

DEV_AUTH_SECRET = "test-dev-auth-secret-0123456789abcdef-EXPLICIT"
DEV_AUTH_HEADERS = {"X-Dev-Auth-Secret": DEV_AUTH_SECRET}


class TestClient(FastAPITestClient):
    """Send the dedicated test dev-auth secret without reusing the JWT signing key."""

    def __init__(self, *args, headers: Mapping[str, str] | None = None, **kwargs):
        merged_headers = dict(headers or {})
        merged_headers.setdefault("X-Dev-Auth-Secret", DEV_AUTH_SECRET)
        # Use a syntactically valid socket peer so production-equivalent proxy trust can remain
        # restricted to explicit IP/CIDR entries instead of permitting the TestClient hostname.
        kwargs.setdefault("client", ("127.0.0.1", 50000))
        super().__init__(*args, headers=merged_headers, **kwargs)
