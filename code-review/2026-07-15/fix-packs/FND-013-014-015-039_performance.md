# Fix Pack — FND-013 / FND-014 / FND-015 / FND-039: Performance & data-durability

**Severity:** Medium (×3), Low (FND-039) · **Domain:** Performance & scalability · **Effort:** M

## Context
`C:\dev\kingfisher-crm`, FastAPI + MySQL (SQLAlchemy 2.x). Two DB writes happen on the common request
path, an audit table grows unbounded, a hot query is unindexed, and an unused relationship is eager-loaded.

---

## FND-013 — Synchronous per-request audit INSERT + redundant JWT decode, unbounded table
**Evidence:**
- `backend/app/main.py:51-60` runs `record_request` on nearly every response (via `run_in_threadpool`).
- `backend/app/request_logging.py:47-68` opens a fresh `SessionLocal`, **re-decodes the JWT** (`:37-44`, already decoded in `get_current_user`), and commits an INSERT.
- Combined with `_touch`'s presence write (`backend/app/auth/deps.py:22-33,63`) that's up to two writes/request.
- `request_logs` has no retention/pruning; its indexes (migration `0005`) don't cover the log viewer's filters (`backend/app/routers/logs.py:24-32` filters on `path LIKE`, `method`, `user_id`, orders by `id desc`).

**Fix direction:**
- Reuse the already-authenticated principal instead of re-decoding: pass the resolved user id from the
  request state (set it in `get_current_user`, read it in `record_request`) — avoid a second JWT decode +
  DB `get`.
- Move audit persistence off the request path: enqueue to an in-process buffer and flush in batches from a
  background task (keep the existing "never break a request" guard).
- Add a retention story: a pruning routine (scheduled job or a `DELETE WHERE created_at < now()-interval`)
  and/or table partitioning; document the retention window.
- Add an index matching the query shape (e.g. `(user_id)`, `(method)`, and consider a prefix index for
  `path`), replacing/augmenting the current ones via a migration.

---

## FND-014 — Unbounded CSV export & uncapped analytics
**Evidence:** `backend/app/routers/users.py:113-158` `export_users_csv` does
`list(db.scalars(_filtered(...)))` with **no limit/offset**, materializing the whole filtered set in memory.
`backend/app/routers/dashboard.py:36-83` runs four aggregate scans, protected only by the loose 600/min
global bucket.

**Fix direction:**
- Stream the CSV: use a `StreamingResponse` with a server-side/yield-per-row cursor and a hard row ceiling
  (e.g. cap at N rows, or require filters). Keep the CSV-injection neutralisation (`_csv_safe`, `users.py:31`).
- Add a tighter per-user throttle or a short (e.g. 30–60s) cache to `/dashboard/analytics`.

---

## FND-015 — No index on `users.last_seen_at`
**Evidence:** `backend/app/models/user.py:38` declares `last_seen_at` with no index; it's filtered/counted
on every dashboard + presence read (`dashboard.py:29,81`, `users.py:80`, `presence.py:19`).

**Fix direction:** add an index on `last_seen_at` (consider composite `(is_active, last_seen_at)` for the
online-count query) via a new Alembic migration `0011_*`. Add the `index=True`/`Index(...)` to the model to
keep model↔schema parity (the app builds schema from migrations; keep both in sync — see `env.py`
`compare_type`/`compare_server_default`).

---

## FND-039 — Unused eager-load on the auth hot path
**Evidence:** `backend/app/models/user.py:59-61` `User.identities` is `lazy="selectin"`, so it's fetched via
an extra query on every `get_current_user` (`db.get(User, ...)`), but the auth path never reads it
(CONFIRMED). `role` and `granted_permissions` selectin-loads *are* needed (permission checks).

**Fix direction:** change `identities` to `lazy="select"` (lazy) or `raiseload`-guard the hot path, and
explicitly eager-load `identities` only where it's rendered (e.g. `UserOut.from_user` call sites that build
`IdentityOut`). Verify no N+1 is introduced in the directory/detail responses (add `selectinload` at those
query sites if needed).

## Constraints — do NOT change
- Keep the `{code,message}` error envelope and the "logging never breaks a request" guarantee.
- Keep CSV formula-injection neutralisation.
- Migrations must have working `downgrade()`; keep single Alembic head.

## Acceptance criteria
- Only **one** DB write on a normal authenticated GET (verify the redundant decode + double write is gone).
- `EXPLAIN` (or a test asserting index usage) shows the online-count query uses the new `last_seen_at` index.
- CSV export streams and enforces a documented row cap.
- `alembic upgrade head` + `downgrade` both work; `alembic heads` still single.
- `docker compose exec api pytest` green (95+); `ruff` clean.
