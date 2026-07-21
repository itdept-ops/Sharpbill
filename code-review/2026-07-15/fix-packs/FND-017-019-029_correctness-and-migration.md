# Fix Pack — FND-017 / FND-019 / FND-029: Correctness bugs + migration safety

**Severity:** Medium (FND-017, -019), Low (FND-029) · **Effort:** S each

## Context
`[repository root]`, FastAPI + MySQL + Alembic. A grab-bag of independently-shippable correctness fixes.

---

## FND-017 — `bulk_action` commit errors escape as 500 with silent partial application
**Evidence:** `backend/app/routers/users.py:184-213` loops committing per item inside `try/except ApiError`
**only**. A `db.commit()` that raises `OperationalError` (deadlock / lock-wait timeout) or `IntegrityError`
is not caught → the batch aborts with a 500 after some items already committed, and the response never says
which succeeded.

**Fix:** catch `sqlalchemy.exc.SQLAlchemyError` (in addition to `ApiError`) per item: on such an error,
`db.rollback()` and append `{"id": uid, "ok": False, "error": "DB_ERROR"}`, so the loop continues and the
response always contains a complete per-id outcome. Keep the existing `ApiError` handling.

---

## FND-019 — Migration 0002 downgrade truncates long custom role names
**Evidence:** `backend/alembic/versions/0002_rbac_presence_kick.py` migrates the `users.role` `String(20)`
column into the RBAC role FK on upgrade; the **downgrade** writes role *names* back into a `String(20)`.
Role names allow up to 50 chars (`backend/app/models/role.py:23`), so a longer custom role name truncates
or errors on downgrade.

**Fix:** in the 0002 `downgrade()`, map any role name that doesn't fit (or isn't a known system role) to a
safe default (e.g. `"user"`) before writing, or widen the restored column to `String(50)`. Add an inline
comment that custom roles don't round-trip through a downgrade. Verify `alembic downgrade` from head works
with a seeded long-named custom role.

---

## FND-029 — Correctness minutiae (batch)
1. **`online=false` filter is a no-op** (CONFIRMED). `backend/app/routers/users.py:79` uses `if online:` —
   `online=False` (offline-only) is treated the same as `None` (no filter), returning *all* users. Fix:
   `if online is not None:` and branch on the boolean (`>= cutoff` for True, `< cutoff`/null for False),
   applied in both `_filtered` and consistently for CSV export.
2. **Unapproved users counted "online"** (CONFIRMED). `backend/app/auth/service.py:130` stamps
   `last_seen_at=_now_naive()` on first-login provisioning even for pending users; `dashboard.py:29,81`
   count `is_active AND last_seen >= cutoff` without `is_approved`. Fix: add `is_approved` to the online
   count predicates (dashboard + analytics), or don't stamp `last_seen_at` until a session is actually
   established.
3. **`seed_demo` never sets `last_seen_at`** → dashboard "online" reads 0 after seeding
   (`backend/app/scripts/seed_demo.py:81-94`). Fix: set a recent `last_seen_at` on a subset of seeded active
   users so the demo dashboard shows realistic presence.
4. **Dual-clock timestamps.** DB uses `CURRENT_TIMESTAMP(6)` (`created_at`/`updated_at`) while the app
   writes UTC-naive (`_now_naive`), and the DB session time zone is never pinned (`backend/app/db.py:14`).
   Fix: pin the connection session time zone to UTC (e.g. `SET time_zone='+00:00'` via an engine
   `connect` event or `init_command`) so DB-generated and app-generated timestamps share a frame.

## Constraints — do NOT change
- Keep the `{code,message}` envelope; reuse `ApiError`.
- Keep single Alembic head; every migration keeps a working `downgrade()`.
- Don't alter the immutable-subject identity model.

## Acceptance criteria
- FND-017: a test simulating a commit failure mid-batch returns 200 with per-id `ok:false` entries (no 500).
- FND-019: `alembic upgrade head` then `downgrade` succeeds with a seeded 30+ char custom role name.
- FND-029: tests for `online=false` returning only offline users; a pending user not counted online;
  seeded dashboard shows non-zero online. Suite green; `ruff` clean.
