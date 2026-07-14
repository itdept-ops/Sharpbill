# Kingfisher CRM

An operator-grade internal web app: Google/Microsoft sign-in, a real **roles + permissions**
system, **live presence**, a **session kill-switch (kick)**, and a Matrix/terminal ("DATASTREAM")
UI. FastAPI + React + MySQL, run locally with Docker Compose.

> **AWS deployment is not wired up** — local only. `deploy/` holds production reference files.

## Features

- **Sign-in** with Google or Microsoft. Tokens are verified **server-side** and identity is
  keyed to the provider's **immutable id** (Google `sub` / Microsoft `oid`) — never the email,
  so a changed provider email can't impersonate another account. The verified id is stored and
  shown in the admin UI.
- **Roles & permissions (RBAC).** Every user has one role; roles hold permissions; admins can
  **create new permissions** and roles and assign them, via a grouped permission-matrix builder.
  Access is read from the DB on every request. Built-in permissions: `users.read`, `users.manage`,
  `roles.manage`, `presence.view`, `presence.kick`, `settings.manage`. System roles are protected.
- **User management.** Rich profiles (title, department, phone, location, timezone, bio) with
  self-service and admin edit; a filterable directory (search, role, status, online); a detailed
  per-user page; role/activation/kick/approve controls.
- **Live presence + kick.** See who's online; force sign-out any user (session revoked next
  request via a per-user token epoch). Deactivation and logout are durable revocations too.
- **Site settings.** Admins choose the sign-up mode (**open / approval / closed**), toggle each
  provider, set the default role, and **approve pending sign-ups** from a queue.
- **Dashboard analytics.** Hand-built SVG charts — sign-ups over 14 days, account-status split,
  users-by-role, and sign-in providers.
- **Pages.** Matrix-rain landing, a Technology showcase, an About page, the themed console
  (dashboard, users, user detail, roles, settings, profile). A "Calm" toggle reduces motion.

## Stack

- **Backend** — Python 3.12, FastAPI, SQLAlchemy 2.x, Alembic, PyMySQL. All routes under `/api`.
- **Frontend** — React 18 + TypeScript + Vite, React Router v6, plain CSS. Vite proxies `/api`.
- **Database** — MySQL 8.0 (`utf8mb4`).

## Quick start

```bash
cp .env.example .env
python -c "import secrets; print(secrets.token_hex(32))"   # paste into SESSION_JWT_SECRET
# To click into the app before OAuth is set up, set DEV_AUTH_ENABLED=true in .env

docker compose up --build -d
docker compose exec api alembic upgrade head

#   http://localhost:5173          → the app (landing page)
#   http://localhost:8000/api/docs → API docs
#   http://localhost:8000/api/health
```

Reset the DB: `docker compose down -v && docker compose up -d && docker compose exec api alembic upgrade head`.

## Signing in

- **Google / Microsoft** buttons appear on the login page only once their client IDs are set
  (`GOOGLE_CLIENT_ID` / `AZURE_CLIENT_ID`, plus the `VITE_*` build vars). Until then they're hidden.
- **Dev login** (local only): with `DEV_AUTH_ENABLED=true`, the login page shows a dev form
  (any email + role). `POST /api/auth/dev` is **only mounted when `APP_ENV=local` and
  `DEV_AUTH_ENABLED=true`**. The first email in `ADMIN_EMAILS` becomes an admin.

## API surface (v1)

| Method | Path | Permission | Purpose |
|---|---|---|---|
| GET | `/api/health` | — | liveness + DB check |
| GET | `/api/auth/config` | — | which sign-in methods are available |
| POST | `/api/auth/google` · `/microsoft` | — | verify ID token → session cookie |
| POST | `/api/auth/dev` | — (local only) | dev login |
| POST | `/api/auth/logout` | — | clear cookie |
| GET | `/api/auth/me` | session | current user |
| GET | `/api/dashboard` | session | stats (incl. online count) |
| GET | `/api/dashboard/analytics` | `users.read` | chart data (roles, providers, signups, status) |
| GET | `/api/users` | `users.read` | directory + filters (`search`, `role_id`, `status`, `online`) |
| GET | `/api/users/{id}` | self or `users.read` | user detail |
| PATCH | `/api/users/{id}/profile` | self or `users.manage` | edit profile fields |
| PATCH | `/api/users/{id}/role` | `users.manage` | reassign role |
| PATCH | `/api/users/{id}/status` | `users.manage` | activate / deactivate |
| POST | `/api/users/{id}/approve` | `users.manage` | approve a pending sign-up |
| GET · PUT | `/api/admin/settings` | `settings.manage` | site settings (signup mode, providers, default role) |
| POST | `/api/users/{id}/kick` | `presence.kick` | force sign-out |
| GET | `/api/roles` · `/api/permissions` | `roles.manage` | list |
| POST | `/api/roles` · `/api/permissions` | `roles.manage` | create |
| PATCH · DELETE | `/api/roles/{id}` | `roles.manage` | edit / delete (custom, unused) |
| GET | `/api/presence/online` | `presence.view` | who's online |
| POST | `/api/presence/heartbeat` | session | stay online |

## Security model

Hardened after an adversarial review pass. Highlights:

- **Identity = provider subject id, never email.** Two providers with the same email are two
  accounts; a changed email never merges or hijacks.
- **No privilege amplification.** You can only grant a role/permission you already hold; system
  roles are admin-only to edit; the `admin` role is locked.
- **Last-admin protection.** The final active admin can't be demoted or deactivated.
- **Durable revocation.** Kick and deactivation both stamp a per-user token epoch, so old
  session cookies are rejected on the next request (and reactivation can't resurrect them).
- **Login CSRF guard** (JSON-only on the login routes) + `SameSite=Lax` cookies.
- Optional `ALLOWED_EMAIL_DOMAINS` / `ALLOWED_AZURE_TENANTS` lock provisioning to your org.

## Layout

```
backend/   FastAPI app, SQLAlchemy models, Alembic migrations (0001 schema, 0002 RBAC), tests
frontend/  React + Vite SPA (DATASTREAM terminal theme)
deploy/    production compose + Caddyfile (reference; not used locally)
.github/   CI: lint, migrate, pytest, frontend build — no deploy job
```

## Common commands

```bash
docker compose exec api alembic upgrade head       # apply migrations
docker compose exec api pytest                     # backend tests
docker compose exec api ruff check .               # lint
docker compose exec web npm run lint               # frontend typecheck + lint
docker compose logs -f api web                     # tail logs
```
