# Kingfisher CRM

Internal web app: Google &amp; Microsoft sign-in, database-managed roles (`admin` / `user`),
an admin user-management screen, and a placeholder dashboard. FastAPI + React + MySQL, run
locally with Docker Compose.

> Full build spec and rationale live in `PLAN.md`; console/AWS walkthroughs in `SETUP.md`
> (in the planning repo). This repo is the running implementation. **AWS deployment is not
> wired up yet — local only.**

## Stack

- **Backend** — Python 3.12, FastAPI, SQLAlchemy 2.x, Alembic, PyMySQL. All routes under `/api`.
- **Frontend** — React 18 + TypeScript + Vite, React Router v6. Vite proxies `/api` to the API (same-origin, no CORS).
- **Database** — MySQL 8.0 (`utf8mb4`).
- **Auth** — providers verify server-side; the app issues its own session JWT in an HttpOnly cookie. Roles are read from the DB per request.

## Quick start

```bash
# 1. Create your env file and a session secret
cp .env.example .env
python -c "import secrets; print(secrets.token_hex(32))"   # paste into SESSION_JWT_SECRET
# To click into the app before OAuth is set up, set DEV_AUTH_ENABLED=true in .env

# 2. Build and start MySQL + API + web
docker compose up --build -d

# 3. Create the schema
docker compose exec api alembic upgrade head

# 4. Open the app
#    http://localhost:5173         → the app
#    http://localhost:8000/api/docs → API docs
#    http://localhost:8000/api/health → { "status": "ok", "database": "ok" }
```

Reset the database: `docker compose down -v && docker compose up -d && docker compose exec api alembic upgrade head`.

## Signing in

- **Google / Microsoft** buttons appear on the login page only once their client IDs are set
  (`GOOGLE_CLIENT_ID` / `AZURE_CLIENT_ID` in `.env`, plus `VITE_*` for the browser). Until
  then they're hidden — that's expected.
- **Dev login** (local only): with `DEV_AUTH_ENABLED=true`, the login page shows a small dev
  form (enter any email + role). It calls `POST /api/auth/dev`, which is **only mounted when
  `APP_ENV=local` and `DEV_AUTH_ENABLED=true`** and never activates otherwise. The first email
  listed in `ADMIN_EMAILS` becomes an admin.

## Layout

```
backend/   FastAPI app, SQLAlchemy models, Alembic migrations, tests
frontend/  React + Vite SPA
deploy/    production compose + Caddyfile (reference; not used locally)
.github/   CI (lint, typecheck, migrate, pytest, build) — no deploy job
scripts/   dev helpers
```

## Common commands

```bash
docker compose exec api alembic upgrade head                       # apply migrations
docker compose exec api alembic revision --autogenerate -m "msg"   # new migration
docker compose exec api pytest                                     # backend tests
docker compose exec api ruff check .                               # lint
docker compose logs -f api web                                     # tail logs
```
