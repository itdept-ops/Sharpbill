import time

import google.auth.transport.requests
from google.auth.exceptions import GoogleAuthError
from google.oauth2 import id_token as google_id_token

from app.auth import ProviderTokenError, VerifiedIdentity
from app.auth.replay import check_replay
from app.config import settings

_transport = google.auth.transport.requests.Request()

_VALID_ISSUERS = {"accounts.google.com", "https://accounts.google.com"}


def verify_google_id_token(raw_token: str) -> VerifiedIdentity:
    if not settings.google_client_id:
        raise ProviderTokenError("Google sign-in is not configured")
    try:
        claims = google_id_token.verify_oauth2_token(
            raw_token,
            _transport,
            audience=settings.google_client_id,
            clock_skew_in_seconds=30,
        )
    except (ValueError, GoogleAuthError) as exc:
        # ValueError = bad signature/expired/wrong audience; GoogleAuthError = cert-fetch /
        # transport failures. Either way it's a 401, never an unhandled 500.
        raise ProviderTokenError(str(exc)) from exc

    if claims.get("iss") not in _VALID_ISSUERS:
        raise ProviderTokenError("wrong issuer")
    if not claims.get("email_verified"):
        raise ProviderTokenError("google email not verified")
    if not claims.get("sub") or not claims.get("email"):
        raise ProviderTokenError("missing sub/email")

    # Single-use: reject a token already presented within its validity window. Extend the guard
    # by the verifier's clock-skew allowance (30s past exp) so it covers the whole window in which
    # the token would still be accepted.
    if check_replay(raw_token, float(claims["exp"]) - time.time() + 30):
        raise ProviderTokenError("token already used")

    return VerifiedIdentity(
        provider="google",
        subject=claims["sub"],
        email=claims["email"].lower(),
        display_name=claims.get("name") or claims["email"],
    )
