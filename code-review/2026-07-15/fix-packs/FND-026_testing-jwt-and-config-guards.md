# Fix Pack — FND-026: Negative tests for JWT validation & security config guards

**Severity:** Medium · **Domain:** Testing & QA · **Effort:** S · **Test-only (no source change expected)**

## Context
`C:\dev\kingfisher-crm`, FastAPI backend. Two pieces of security-critical logic have no tests, so a silent
regression would pass CI:
1. Session-JWT validation (`backend/app/auth/jwt.py:25-32`): `decode_session_token` pins
   `algorithms=["HS256"]` and requires `["exp","iat","sub","jti"]`.
2. Config guards (`backend/app/config.py`): secret-strength validator (`:33-38`), prod secure-cookie
   invariant (`:40-44`), and the dev-auth gate `is_dev_auth_enabled` (`:63-65`, local-only).

The existing suite runs against real MySQL via `backend/tests/conftest.py` (95 tests pass).

## Fix direction — add tests
Create `backend/tests/test_jwt.py`:
- A token signed with the wrong secret → `decode_session_token` raises `InvalidTokenError`.
- A token signed with `alg=none` or `HS512`/`RS256` → rejected (alg-confusion guard).
- A token missing each required claim (`exp`,`iat`,`sub`,`jti`) → rejected.
- An expired token (`exp` in the past) → rejected.
- A tampered payload (valid structure, bad signature) → rejected.
- A round-trip: `create_session_token` → `decode_session_token` returns the expected `sub`/`jti`.

Create `backend/tests/test_config.py` (instantiate `Settings` with explicit kwargs / env, not the singleton):
- `SESSION_JWT_SECRET` shorter than 32 chars, or containing `replace-me` → `ValidationError`.
- `APP_ENV=production` with `COOKIE_SECURE=false` → `ValidationError`.
- `is_dev_auth_enabled` is False when `APP_ENV=production` even if `DEV_AUTH_ENABLED=true`; True only when both local + flag.

Optionally add an integration test asserting `POST /api/auth/dev` is **not routed** (404) when the app is
built with `APP_ENV=production` (guards `backend/app/main.py:103`), if feasible within the test harness.

## Constraints — do NOT change
- Prefer not to modify source; if a validator isn't easily unit-testable in isolation, construct `Settings`
  directly with kwargs rather than changing production code.
- Keep using the real-DB harness conventions in `conftest.py` for any HTTP-level test.

## Acceptance criteria
- `backend/tests/test_jwt.py` and `backend/tests/test_config.py` added and passing.
- `docker compose exec api pytest` count rises accordingly and stays green.
- `ruff check . && ruff format --check .` clean.
