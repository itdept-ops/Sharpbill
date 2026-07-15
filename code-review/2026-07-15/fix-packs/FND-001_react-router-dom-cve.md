# Fix Pack — FND-001: Patch the react-router-dom High CVE

**Severity:** High · **Domain:** Dependencies / supply chain · **Effort:** XS

## Context (zero prior knowledge assumed)
Repo: `C:\dev\kingfisher-crm`, a FastAPI + React app. The frontend lives in `frontend/`.
`npm audit` on `frontend/` reports **8 vulnerabilities**; the only **production** one is:

- `react-router-dom@6.30.1` (and its deps `react-router`, `@remix-run/router`) — **High**, open-redirect / XSS:
  - GHSA-2w69-qvjg-hvjx (CVSS 8.0), GHSA-9jcx-v3wj-wh4m, GHSA-2j2x-hqr9-3h42
  - **Fix: `react-router-dom@6.30.4`** — semver-**minor**, non-breaking.

## Evidence
- `frontend/package.json:17` — `"react-router-dom": "6.30.1"`
- App uses router redirects: `frontend/src/App.tsx` (`<Navigate>`), `frontend/src/pages/LoginPage.tsx:14` (`from` route state → `<Navigate to={from}>`), `frontend/src/auth/RequirePermission.tsx`, `frontend/src/auth/ProtectedRoute.tsx`.

## Fix direction
1. In `frontend/package.json`, change `react-router-dom` to `6.30.4` (keep it exact-pinned to match the repo's pin discipline — the other prod deps use exact versions).
2. `cd frontend && npm install` to update `package-lock.json`.
3. Re-run `npm audit` and confirm the react-router advisories are gone (the remaining vite/vitest/esbuild advisories are **dev-only** and out of scope for this pack — do NOT force-upgrade them; that path is semver-major).

## Constraints — do NOT change
- Do not bump React, Vite, or Vitest here (separate, breaking).
- Do not change any routing code — the fix is dependency-only; 6.30.4 is API-compatible with 6.30.1.
- Keep the exact-version pin style (no caret) for this dependency.

## Acceptance criteria
- `frontend/package.json` shows `react-router-dom` `6.30.4`; `package-lock.json` updated.
- `npm audit` no longer lists `react-router-dom` / `react-router` / `@remix-run/router`.
- `cd frontend && npm run lint && npm run test && npm run build` all pass.
- `npm run test` (vitest, 11 tests incl. `RequirePermission.test.tsx`) still green.
