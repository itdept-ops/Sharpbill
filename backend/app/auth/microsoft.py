import re
import time
from typing import Any

import jwt

from app.auth import ProviderTokenError, ProviderUnavailableError, VerifiedIdentity
from app.auth.nonce import consume_nonce
from app.auth.provider_resilience import (
    ProviderKeyCache,
    fetch_json_document,
    microsoft_jwks_ids,
    verification_slot,
)
from app.auth.replay import check_replay
from app.config import settings

_JWKS_URL = "https://login.microsoftonline.com/common/discovery/v2.0/keys"
_jwks_cache = ProviderKeyCache(
    provider="Microsoft",
    fetch_document=lambda: fetch_json_document(_JWKS_URL),
    key_ids=microsoft_jwks_ids,
)
_UUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


def _microsoft_signing_key(raw_token: str) -> jwt.PyJWK:
    try:
        header = jwt.get_unverified_header(raw_token)
    except jwt.PyJWTError as exc:
        raise ProviderTokenError("malformed token header") from exc
    kid = header.get("kid")
    if header.get("alg") != "RS256" or not isinstance(kid, str):
        raise ProviderTokenError("unsupported token signing header")

    document = _jwks_cache.document_for_kid(kid)
    keys = document["keys"]
    matching_key: dict[str, Any] | None = next(
        (key for key in keys if isinstance(key, dict) and key.get("kid") == kid), None
    )
    if matching_key is None:  # Defensive: ProviderKeyCache already established membership.
        raise ProviderTokenError("unknown signing key id")
    try:
        signing_key = jwt.PyJWK.from_dict(matching_key)
    except (jwt.PyJWTError, ValueError) as exc:
        raise ProviderUnavailableError("Microsoft returned an unusable signing key") from exc
    if signing_key.algorithm_name != "RS256":
        raise ProviderUnavailableError("Microsoft returned an unexpected signing-key algorithm")
    return signing_key


def _verified_claims(raw_token: str) -> dict[str, Any]:
    try:
        signing_key = _microsoft_signing_key(raw_token)
        return jwt.decode(
            raw_token,
            signing_key.key,
            algorithms=["RS256"],  # pinned; never 'none'/HS*
            audience=settings.azure_client_id,
            leeway=30,
            options={"require": ["exp", "iat", "aud", "iss", "sub"], "verify_iss": False},
        )
    except ProviderUnavailableError:
        raise
    except ProviderTokenError:
        raise
    except (jwt.PyJWTError, ValueError) as exc:
        raise ProviderTokenError(str(exc)) from exc


def _identity_from_claims(raw_token: str, claims: dict[str, Any]) -> VerifiedIdentity:
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

    # Nonce binding: the token must carry a `nonce` this app issued, consumed exactly once
    # (single-use, DB-backed) — binds the token to our login request and defeats replay/injection.
    if not consume_nonce(claims.get("nonce", "")):
        raise ProviderTokenError("missing or invalid nonce")

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


def verify_microsoft_id_token(raw_token: str) -> VerifiedIdentity:
    if not settings.azure_client_id:
        raise ProviderTokenError("Microsoft sign-in is not configured")
    with verification_slot():
        claims = _verified_claims(raw_token)
    return _identity_from_claims(raw_token, claims)
