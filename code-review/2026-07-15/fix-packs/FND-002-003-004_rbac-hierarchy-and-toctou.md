# Fix Pack — FND-002 / FND-003 / FND-004: RBAC hierarchy + last-admin race

**Severity:** Medium (×3) · **Domain:** Security: Authorization / RBAC · **Effort:** M
**Do these together** — they touch the same two routers and share one new test module.

## Context
`C:\dev\kingfisher-crm`, FastAPI backend under `backend/`. RBAC model: a user has one role (roles hold
permissions) plus optional direct grants. `user.permission_keys` = role perms ∪ direct grants
(`backend/app/models/user.py:76`). The `admin` role holds all built-in permissions; system roles are
protected. Permission gates live in `backend/app/auth/deps.py:67` (`require_permission`).

The mutating user/role endpoints correctly block **self**-modification and correctly block granting a
permission the actor doesn't hold, but they do **not** check whether the *target* outranks the actor,
and the last-admin guard is a non-atomic check-then-act.

---

## FND-002 — A non-admin delegate can disable/demote/kick every admin but the last

**Evidence** (`backend/app/routers/users.py`):
- `update_status` (:313) — gated only by `require_permission(USERS_MANAGE)`; only guards are self-check (:320) and `_active_admin_count()<=1` when deactivating (:324). With ≥2 admins it deactivates a full admin (:326-327).
- `update_role` (:257) — subset guard (:270) checks only that the *new* role's perms ⊆ actor's; demotes a non-last admin.
- `bulk_action` (:161) — same gaps for `deactivate`/`assign_role`.
- `kick_user` (:342) — gated only by `require_permission(PRESENCE_KICK)`; self-check (:348) then session kill (:351) + `revoke_all_for_user` (:353). **No admin protection, no last-admin floor at all.**
- The seeded non-admin `Manager` role holds `presence.kick` (`backend/app/scripts/seed_demo.py:56-61`), so an ordinary Manager can kick any admin.

**Fix direction:** add a **target-seniority guard** applied in `update_status`, `update_role`,
`bulk_action`, and `kick_user`: a non-admin actor must not act on a target whose *effective* permission
set is not a subset of the actor's (equivalently, never on an `admin`-role target). Implement once as a
helper, e.g. `_assert_can_target(actor, target)` that raises `ApiError(403, "INSUFFICIENT_PRIVILEGE", …)`
when `not _is_admin(actor) and not target.permission_keys <= actor.permission_keys`. Call it after the
self-check in each endpoint (and per-item in bulk). Keep the existing last-admin count guard as a backstop.

---

## FND-003 — Last-admin protection is a TOCTOU race

**Evidence:** `_active_admin_count` (`users.py:47-56`) is a plain `SELECT COUNT(*)` with no row lock. Each
guard reads the count then mutates a *different* row (update_status :324→:326-327; update_role :276→:278;
bulk :192→:195). Sync endpoints run in the threadpool on separate connections at InnoDB REPEATABLE READ,
so two concurrent deactivations of two distinct admins both see count==2, both pass `<=1`, both commit → 0
admins. (`get_db` yields a fresh session per request: `backend/app/db.py:23`.)

**Fix direction:** make count-and-mutate atomic. Preferred: within the same transaction, lock the admin
rows before deciding — `SELECT id FROM users JOIN roles … WHERE role=admin AND is_active AND is_approved
FOR UPDATE` — or perform the demote/deactivate as a single conditional `UPDATE … WHERE (SELECT COUNT(active
admins) > 1)` and check `rowcount`. Optionally add a DB-level backstop (trigger / generated-column partial
index asserting ≥1 active admin). The per-item re-count inside a single `/bulk` call is not sufficient
across concurrent requests.

---

## FND-004 — roles.manage delegate can rewrite/delete roles above their privilege

**Evidence** (`backend/app/routers/roles.py`):
- `update_role` (:140) and `delete_role` (:175) require only `roles.manage`. `_guard_grantable` (:62)
  checks only that the *new* permission set ⊆ actor's — not the role's *current* privilege. So a non-admin
  delegate can strip a high-privilege custom role to fewer/empty perms (mass-revoking from all holders) or
  delete any unused custom role regardless of its privilege.

**Fix direction:** before allowing edit/delete of a **non-system** role, require the actor to already hold
(be a superset of) the role's *existing* permission set (unless `_is_admin(actor)`). Add this alongside the
existing grant-subset check on the new set.

---

## Constraints — do NOT change
- Do not weaken the existing self-modification blocks or the grant-subset (`_guard_grantable`) checks.
- Do not change the meaning of `admin` being all-powerful, or system-role protection.
- Keep the `{ "detail": { "code", "message" } }` error envelope (`backend/app/errors.py`); reuse `ApiError`.
- Preserve current passing behaviour: an admin can still do everything; a delegate can still manage
  users/roles that are at or below their privilege.

## Acceptance criteria
- New `backend/tests/test_admin_protection.py` proving:
  1. a non-admin holding only `presence.kick` (the seeded `Manager`) gets **403** kicking an `admin`;
  2. a non-admin `users.manage` holder gets **403** deactivating/demoting an `admin`;
  3. a `roles.manage` non-admin gets **403** editing/deleting a custom role whose perms they don't all hold;
  4. an **admin** can still perform all of the above.
- A concurrency test (or a documented DB-level guarantee) showing two simultaneous admin-deactivations cannot both succeed when only two admins exist.
- Full suite green: `docker compose exec api pytest` (currently 95 passing — keep them passing).
- `ruff check . && ruff format --check .` clean.
