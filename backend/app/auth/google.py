import time
from collections.abc import Mapping
from typing import Any

import jwt
from google.auth.exceptions import GoogleAuthError
from google.oauth2 import id_token as google_id_token

from app.auth import ProviderTokenError, ProviderUnavailableError, VerifiedIdentity
from app.auth.nonce import consume_nonce
from app.auth.provider_resilience import (
    ProviderKeyCache,
    fetch_json_document,
    google_certificate_ids,
    verification_slot,
)
from app.auth.replay import check_replay
from app.config import settings

_VALID_ISSUERS = {"accounts.google.com", "https://accounts.google.com"}
_CERTS_URL = "https://www.googleapis.com/oauth2/v1/certs"

_certificate_cache = ProviderKeyCache(
    provider="Google",
    fetch_document=lambda: fetch_json_document(_CERTS_URL),
    key_ids=google_certificate_ids,
)


class _CachedResponse:
    status = 200
    headers: Mapping[str, str] = {}

    def __init__(self, document: Mapping[str, Any]) -> None:
        import json

        self.data = json.dumps(document, separators=(",", ":")).encode("utf-8")


class _CachedGoogleRequest:
    """google-auth transport that can only serve our bounded certificate cache."""

    def __init__(self, raw_token: str) -> None:
        self._raw_token = raw_token

    def __call__(self, url: str, **_kwargs: Any) -> _CachedResponse:
        if url != _CERTS_URL:
            raise ProviderUnavailableError("Google requested an unexpected certificate endpoint")
        try:
            header = jwt.get_unverified_header(self._raw_token)
        except jwt.PyJWTError as exc:
            raise ProviderTokenError("malformed token header") from exc
        kid = header.get("kid")
        if header.get("alg") != "RS256" or not isinstance(kid, str):
            raise ProviderTokenError("unsupported token signing header")
        return _CachedResponse(_certificate_cache.document_for_kid(kid))


def verify_google_id_token(raw_token: str) -> VerifiedIdentity:
    if not settings.google_client_id:
        raise ProviderTokenError("Google sign-in is not configured")
    with verification_slot():
        try:
            claims = google_id_token.verify_oauth2_token(
                raw_token,
                _CachedGoogleRequest(raw_token),
                audience=settings.google_client_id,
                clock_skew_in_seconds=30,
            )
        except ProviderUnavailableError:
            raise
        except ProviderTokenError:
            raise
        except (GoogleAuthError, ValueError) as exc:
            raise ProviderTokenError(str(exc)) from exc

    if claims.get("iss") not in _VALID_ISSUERS:
        raise ProviderTokenError("wrong issuer")
    if not claims.get("email_verified"):
        raise ProviderTokenError("google email not verified")
    if not claims.get("sub") or not claims.get("email"):
        raise ProviderTokenError("missing sub/email")

    # Nonce binding: the token must carry a `nonce` this app issued for a pending sign-in, and it
    # is consumed exactly once (single-use, DB-backed). This binds the token to our login request
    # and defeats id_token replay/injection even across workers/instances.
    if not consume_nonce(claims.get("nonce", "")):
        raise ProviderTokenError("missing or invalid nonce")

    # Single-use: reject a token already presented within its validity window. Extend the guard
    # by the verifier's clock-skew allowance (30s past exp) so it covers the whole window in which
    # the token would still be accepted.
    if check_replay(raw_token, float(claims["exp"]) - time.time() + 30):
        raise ProviderTokenError("token already used")

    hosted_domain = claims.get("hd")
    return VerifiedIdentity(
        provider="google",
        subject=claims["sub"],
        email=claims["email"].lower(),
        display_name=claims.get("name") or claims["email"],
        hosted_domain=hosted_domain.lower() if isinstance(hosted_domain, str) else None,
    )
