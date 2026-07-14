# deploy/ — production reference (NOT wired up)

These files are the intended production topology from the plan. **AWS deployment is not set
up in this repo yet** — nothing here runs automatically and the CI workflow has no deploy
job. They're kept as a reference for when deployment happens:

- `docker-compose.prod.yml` — Caddy (TLS) + api + web (nginx) on a single host.
- `Caddyfile` — reverse proxy: `/api/*` → api, everything else → the SPA.

Prod images build from the `prod` target of each Dockerfile. See `PLAN.md` / `SETUP.md` in
the planning repo for the full AWS walkthrough.
