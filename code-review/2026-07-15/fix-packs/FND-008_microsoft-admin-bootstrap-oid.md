# Fix Pack — FND-008: Bootstrap Microsoft admin on immutable oid, not the email claim

**Severity:** Medium · **Domain:** Security: Authentication · **Effort:** M

## Context
`[repository root]`, FastAPI backend. Microsoft ID tokens are verified in
`backend/app/auth/microsoft.py`; admin bootstrap decision is in `backend/app/auth/service.py`.
Identity is correctly keyed on the immutable `oid` for *account* lookup, but the *admin-bootstrap*
decision keys on the email claim.

## Evidence
- `backend/app/auth/microsoft.py:47` — `email = (claims.get("email") or claims.get("preferred_username") or "").lower()`. There is **no `email_verified` equivalent** (Google enforces `email_verified` at `google.py:33`; Microsoft has none here). `preferred_username`/UPN is tenant-mutable.
- `backend/app/auth/service.py:39-55` — `_admin_bootstrap` returns True when `ident.email in settings.admin_email_set` and (google, or microsoft with `tenant_id == azure_admin_tenant_id`). So a Microsoft admin bootstrap trusts the unverified email/UPN.

## Fix direction
Introduce an **object-id allowlist** for Microsoft admin bootstrap rather than trusting the email claim:
- Add a config field (e.g. `azure_admin_object_ids: str` in `backend/app/config.py`, comma-separated, with a `set` property) mirroring the existing `admin_emails` pattern.
- In `_admin_bootstrap`, for `provider == "microsoft"`, require `ident.subject` (the `oid`) to be in that allowlist **and** the tenant to match — do not use the email at all for the Microsoft admin decision.
- Keep Google bootstrap as-is (Google's `email_verified` is enforced upstream, so email is a verified claim there).
- Document the new env var in `.env.example` next to `AZURE_ADMIN_TENANT_ID` and update the README's admin-bootstrap description.

If introducing a new config var is undesirable, the weaker alternative is to require a Microsoft-verified
signal, but MS ID tokens don't carry `email_verified` — the oid allowlist is the robust fix.

## Constraints — do NOT change
- Don't change account *lookup* keying (already correctly on `(provider, oid)`).
- Don't break Google admin bootstrap.
- Keep `VerifiedIdentity` (`backend/app/auth/__init__.py`) shape or extend it additively.

## Acceptance criteria
- With the oid allowlist empty, no Microsoft login is bootstrapped as admin even if its email is in `ADMIN_EMAILS`.
- With a matching oid + tenant, the Microsoft login bootstraps admin.
- New test in `backend/tests/test_auth.py` covering both.
- `.env.example` + README updated. Suite green; `ruff` clean.
