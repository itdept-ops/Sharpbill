# Fix Pack — FND-021: Add security response headers to the SPA

**Severity:** Medium · **Domain:** Frontend / Ops · **Effort:** S · **Scope:** `frontend/nginx.conf` only

## Context
`C:\dev\kingfisher-crm`. The production frontend image (`frontend/Dockerfile:21-23`, `prod` stage) serves
the built SPA with nginx. The config ships with no security headers:

```
# frontend/nginx.conf (current)
server {
  listen 80;
  root /usr/share/nginx/html;
  index index.html;
  location / { try_files $uri /index.html; }
}
```

The app also loads an external Google Identity script (`frontend/index.html:34`,
`https://accounts.google.com/gsi/client`), so a CSP must permit it.

## Fix direction
Add a hardened header block to `nginx.conf`. Suggested baseline (tune the CSP to the app's real needs):

```
add_header X-Frame-Options "DENY" always;
add_header X-Content-Type-Options "nosniff" always;
add_header Referrer-Policy "strict-origin-when-cross-origin" always;
add_header Permissions-Policy "geolocation=(self), camera=(), microphone=()" always;
add_header Content-Security-Policy "default-src 'self'; script-src 'self' https://accounts.google.com; frame-src https://accounts.google.com; connect-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'" always;
```

Notes:
- `geolocation=(self)` is required — the app captures opt-in GPS (`frontend/src/util/location.ts`).
- `style-src 'unsafe-inline'` is needed because the app uses inline `style={...}` and CSS variables; if you
  want to drop it, that's a larger refactor — out of scope here.
- Verify the Google sign-in button still renders and posts (the GSI script + its iframe must be allowed).
- Do **not** add HSTS here (TLS terminates at the edge/Caddy, out of scope); leave HSTS to the edge.

## Constraints — do NOT change
- Don't touch `deploy/` (Caddy) — this is the in-repo nginx image only.
- Keep the SPA history fallback (`try_files $uri /index.html`).
- Don't break Google sign-in (test the CSP against the live login flow).

## Acceptance criteria
- `curl -I` against the built prod image shows the new headers.
- The app loads and Google sign-in still works with the CSP applied (no CSP violations in the browser console for the normal flow).
- No change to `docker-compose.yml` dev behaviour (dev uses Vite, not nginx).
