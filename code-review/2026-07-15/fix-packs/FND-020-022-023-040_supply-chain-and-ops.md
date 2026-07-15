# Fix Pack — FND-020 / FND-022 / FND-023 / FND-040: Supply chain, CI & container ops

**Severity:** Medium (FND-020, -022, -023), Low (FND-040) · **Domain:** Ops & supply chain · **Effort:** S–M
**Scope note:** AWS deployment is out of scope. These target the in-repo `docker-compose.yml`, both
`Dockerfile`s, `nginx`, and CI only — NOT the `deploy/` directory or how the app ships to AWS.

---

## FND-020 — Vulnerable backend deps + no lockfile/hashes
**Evidence:** `pip-audit` on `backend/requirements.txt` (pinned): `PyJWT 2.10.1` (signs the session JWT,
`backend/app/auth/jwt.py`) → PYSEC-2026-120/175-179 (fix 2.13.0); `cryptography 44.0.2` → PYSEC-2026-35/2141
+ GHSA-537c-gmf6-5ccf; `requests 2.32.3` → PYSEC-2026-1872/2275; `starlette 0.46.2` (FastAPI core) → 7
advisories. Direct deps are pinned but there's no lockfile / hashes, so transitive packages (incl. Starlette,
pulled by FastAPI) float.

**Fix:** bump PyJWT → 2.13.0, cryptography → latest 4x patch, requests → 2.32.4+, and Starlette to a fixed
version compatible with the pinned FastAPI (verify FastAPI's supported Starlette range; bump FastAPI if
needed). Adopt a hash-pinned lock (`pip-compile` from a `requirements.in`, or migrate to `uv`). Re-run
`pip-audit` to confirm zero highs. Run the full suite after — these are on the auth/crypto path.

---

## FND-022 — CI builds no prod image and has no dependency-audit gate
**Evidence:** `.github/workflows/ci.yml` runs lint/tests/`vite build` and an e2e compose using the **dev**
Dockerfile stages, but never builds either `Dockerfile`'s `prod` target, and runs neither `npm audit` nor
`pip-audit`. This is why FND-001/FND-020 went unflagged.

**Fix:** add to the `test` job (or a new job): `docker build --target prod backend/` and
`docker build --target prod frontend/` (smoke that the shipped artifact builds); add an `npm audit
--audit-level=high` step in `frontend/` and a `pip-audit -r backend/requirements.txt` step, failing on High.
Allowlist any accepted dev-only advisories explicitly (e.g. the vite/vitest chain) so the gate is meaningful.

---

## FND-023 — Root containers + per-worker in-memory state
**Evidence:** neither `Dockerfile` has a `USER` directive (all stages run as root). `backend/Dockerfile:24`
prod stage hardcodes `uvicorn --workers 2`, but the rate-limiter (`backend/app/ratelimit.py`), replay guard
(`backend/app/auth/replay.py`), and presence hub (`backend/app/routers/ws.py:26`) are per-process — so limits
double, replay isn't shared, and the roster splits across workers.

**Fix:**
- Add a non-root user to both images (create a user, `chown` the app dir, `USER app`) in the prod stages
  (dev stages bind-mount source, so weigh the dev-UX tradeoff).
- Either run a single worker until the in-memory state is externalised, **or** move rate-limit/replay/presence
  to shared state (Redis). At minimum, add a code comment + README note that `--workers > 1` weakens those
  mechanisms today.

---

## FND-040 — Ops hygiene (batch)
**Evidence & fixes:**
- **Unpinned base images:** `frontend/Dockerfile:21` `FROM nginx:alpine` (no version). Pin to a specific
  digest/tag; consider pinning `python:3.12-slim` / `node:24-alpine` to digests too.
- **No `api` healthcheck in compose:** `docker-compose.yml:23-37` — `web` waits only for
  `service_started`; the DB-aware `/api/health` (`backend/app/routers/health.py`) is unused by compose.
  Add a `healthcheck` to the `api` service (curl `/api/health`) and make `web` depend on
  `service_healthy`.
- **Non-hermetic build:** `backend/Dockerfile:6-9` fetches an external cert bundle at build time; make it
  optional/cached so offline builds work (only needed when `DB_REQUIRE_TLS=true`). *(Kept AWS-agnostic:
  fix the build hermeticity, not the deploy path.)*
- **Pin discipline:** `frontend/package.json:14` `@azure/msal-browser` is on a caret range while peers are
  exact-pinned; pin it exact to match.

## Constraints — do NOT change
- Don't touch `deploy/` or AWS deployment config.
- Keep dev-stage hot-reload working (bind mounts).
- Keep the app functionally identical (these are packaging/CI/security-hardening changes).

## Acceptance criteria
- `pip-audit -r backend/requirements.txt` and `npm audit --audit-level=high` report no High findings (or
  only explicitly-allowlisted dev-only ones); CI enforces this.
- `docker build --target prod` succeeds for both images in CI.
- `docker compose exec api pytest` still green (95+) after the backend dep bumps.
- Containers run as non-root in the prod stages (`docker inspect` / `whoami`).
- `docker compose up` still comes up healthy with the new `api` healthcheck.
