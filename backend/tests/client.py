"""Test HTTP client that explicitly authenticates to the local-only dev-login seam."""

from collections.abc import Mapping

from fastapi.testclient import TestClient as FastAPITestClient

from app.legal_acceptance import CURRENT_LEGAL_BUNDLE_VERSION

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

    def request(self, method: str, url, **kwargs):
        """Default the current legal bundle in existing happy-path login fixtures.

        Tests can exercise rejection by explicitly passing either field with a false/stale value.
        This convenience is confined to the custom test client; production schemas require both.
        """
        if method.upper() == "POST" and str(url) in {
            "/api/auth/google",
            "/api/auth/microsoft",
            "/api/auth/dev",
        }:
            payload = kwargs.get("json")
            if isinstance(payload, dict):
                payload = dict(payload)
                payload.setdefault("legal_accepted", True)
                payload.setdefault("legal_bundle_version", CURRENT_LEGAL_BUNDLE_VERSION)
                kwargs["json"] = payload
        return super().request(method, url, **kwargs)
