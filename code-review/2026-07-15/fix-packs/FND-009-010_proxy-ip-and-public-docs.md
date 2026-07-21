# Fix Pack — FND-009 / FND-010: Real client IP + gate the public API docs

**Severity:** Medium (×2) · **Domain:** Security / observability · **Effort:** S–M

---

## FND-009 — Per-IP rate-limit & audit source-IP collapse to the proxy container IP

### Context / evidence
`[repository root]`. `backend/app/main.py:73` keys rate-limit buckets on
`request.client.host` (the socket peer), and `backend/app/request_logging.py:29` deliberately ignores
`X-Forwarded-For`. But in the compose topology the browser talks to the Vite/web container, which proxies
`/api` → `api:8000` (`frontend/vite.config.ts:32`, `changeOrigin:false`, `ws:true`). So the socket peer
for every API request is the **web container's** IP → all users share one login/api bucket and one logged IP.

### Fix direction
- Add Starlette/Uvicorn proxy-header handling **scoped to trusted hosts**: mount
  `uvicorn.middleware.proxy_headers.ProxyHeadersMiddleware` (or run uvicorn with `--proxy-headers
  --forwarded-allow-ips=<trusted proxy CIDR>`), so `request.client.host` is rewritten from `X-Forwarded-For`
  **only** when the immediate peer is the trusted proxy.
- Keep the "distrust raw XFF from untrusted clients" property — the point is to trust it *only* from the
  known proxy. Do **not** blindly read `X-Forwarded-For` in app code.
- Document the requirement (the app must sit behind a proxy that sets XFF and be told that proxy's IP).
- The comment at `request_logging.py:30-34` already anticipates exactly this — implement it.

### Constraints
- Do not trust XFF unconditionally (that would let any direct client spoof the source IP — the current
  code correctly avoids this).
- Keep the rate-limit and audit behaviour identical when no trusted proxy is configured.

### Acceptance
- With a configured trusted proxy sending `X-Forwarded-For`, `request.client.host` (and thus the rate-limit
  key and logged IP) reflects the real client, verified by a test using `TestClient` with the header +
  trusted-ips config.
- Without the config, behaviour is unchanged (socket peer). Suite green.

---

## FND-010 — Interactive API docs & OpenAPI schema are public to anonymous users

### Context / evidence
`backend/app/main.py:16-20` sets `docs_url="/api/docs"` and `openapi_url="/api/openapi.json"` with no auth;
`backend/app/request_logging.py:12` also excludes them from the audit log. For an access-control console
the full route/permission map is handed to any anonymous visitor and isn't recorded.

### Fix direction (pick one)
- **Simplest:** disable docs/openapi outside local — set `docs_url`/`openapi_url` to `None` unless
  `settings.app_env == "local"`.
- **Or** keep them but gate behind auth: serve a custom `/api/docs` + `/api/openapi.json` protected by a
  dependency (e.g. any authenticated user, or `settings.manage`), using FastAPI's
  `get_swagger_ui_html` / `app.openapi()`.
- If kept enabled anywhere, remove `/api/docs` and `/api/openapi` from the audit-skip prefixes so access is recorded.

### Constraints
- Don't break local dev discoverability (keep docs available when `app_env == "local"`).
- Keep the `/api` prefix and the existing route registrations intact.

### Acceptance
- In a non-local config, `GET /api/docs` and `GET /api/openapi.json` return 404/401 (per chosen approach).
- In local, docs still load. Suite green; `ruff` clean.
