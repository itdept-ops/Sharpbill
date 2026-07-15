import re
import time

import jwt
from jwt import PyJWKClient

from app.auth import ProviderTokenError, VerifiedIdentity
from app.auth.replay import check_replay
from app.config import settings

_JWKS_URL = "https://login.microsoftonline.com/common/discovery/v2.0/keys"
_jwks_client = PyJWKClient(_JWKS_URL, cache_keys=True, lifespan=3600)
_UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


def verify_microsoft_id_token(raw_token: str) -> VerifiedIdentity:
    if not settings.azure_client_id:
        raise ProviderTokenError("Microsoft sign-in is not configured")
    try:
        signing_key = _jwks_client.get_signing_key_from_jwt(raw_token)
        claims = jwt.decode(
            raw_token,
            signing_key.key,
            algorithms=["RS256"],  # pinned; never 'none'/HS*
            audience=settings.azure_client_id,
            leeway=30,
            options={"require": ["exp", "iat", "aud", "iss", "sub"], "verify_iss": False},
        )
    except (jwt.PyJWTError, ValueError) as exc:
        # Covers InvalidTokenError plus PyJWKClientError (JWKS fetch / kid resolution failures),
        # so a provider-side hiccup becomes a clean 401 rather than an unhandled 500.
        raise ProviderTokenError(str(exc)) from exc

    # Multi-tenant issuer validation: with the 'common' authority, 'iss' is per-tenant.
    # Take 'tid' from the (signature-verified) token, ensure it is a UUID, and require an
    # exact match against the issuer template.
    tid = claims.get("tid", "")
    if not _UUID_RE.fullmatch(tid):
        raise ProviderTokenError("missing or malformed tid")
    if claims["iss"] != f"https://login.microsoftonline.com/{tid}/v2.0":
        raise ProviderTokenError("issuer does not match tenant")

    oid = claims.get("oid", "")
    if not _UUID_RE.fullmatch(oid):
        raise ProviderTokenError("missing or malformed oid")

    email = (claims.get("email") or claims.get("preferred_username") or "").lower()
    if "@" not in email:
        raise ProviderTokenError("no usable email claim")

    # Single-use: reject a token already presented within its validity window. Extend the guard
    # by the verifier's leeway (30s past exp) so it covers the whole window the token is accepted.
    if check_replay(raw_token, float(claims["exp"]) - time.time() + 30):
        raise ProviderTokenError("token already used")

    return VerifiedIdentity(
        provider="microsoft",
        subject=oid,  # stable directory object id; never 'sub' (pairwise per app)
        email=email,
        display_name=claims.get("name") or email,
        tenant_id=tid,
    )
