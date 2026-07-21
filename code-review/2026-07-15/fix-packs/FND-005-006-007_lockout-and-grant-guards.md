# Fix Pack — FND-005 / FND-006 / FND-007: Auth-logic footguns (Wave 0 quick wins)

**Severity:** Medium (×3) · **Effort:** XS each · Three small, independent edits in the auth/settings layer.

## Context
`[repository root]`, FastAPI backend under `backend/`. Login/provisioning logic is in
`backend/app/auth/service.py`; site settings in `backend/app/routers/settings.py`; role/permission
management in `backend/app/routers/roles.py`.

---

## FND-005 — Nobody (even admin) can attach a runtime-created custom permission to a role

**Evidence:** `backend/app/routers/roles.py:62` `_guard_grantable(actor, keys)` raises 403 when
`set(keys) - actor.permission_keys` is non-empty, with **no admin bypass**. An admin's `permission_keys`
(`backend/app/models/user.py:76`) is only their role's built-ins ∪ direct grants; a **just-created custom
permission is attached to no role**, so it's in nobody's set — and `create_role`/`update_role` (which call
`_guard_grantable`) 403 when trying to attach it. Compare `set_user_permissions`
(`backend/app/routers/users.py:303`) which *does* have `if not _is_admin(actor)`. The docstring at
`roles.py:64` wrongly assumes "a full admin holds every permission."

**Fix:** add the admin bypass to `_guard_grantable`:
```python
def _guard_grantable(actor: User, keys: list[str]) -> None:
    if _is_admin(actor):
        return
    extra = set(_normalize(keys)) - actor.permission_keys
    if extra:
        raise ApiError(403, "INSUFFICIENT_PRIVILEGE", "You can only grant permissions you hold; missing: " + ", ".join(sorted(extra)))
```
(`_is_admin` already exists in `roles.py:22`.)

---

## FND-006 — `signup_mode="closed"` blocks the ADMIN_EMAILS bootstrap (recovery lockout)

**Evidence:** `backend/app/auth/service.py:113` raises `SIGNUP_CLOSED` **before** the admin-bootstrap block
at `:116` (`_admin_bootstrap(ident)`). A trusted, provider-verified admin email cannot provision when
signup is closed — the one path meant to guarantee admin access.

**Fix:** compute `is_admin_boot = _admin_bootstrap(ident)` **before** the closed-mode check, and allow a
bootstrap admin through even when `site.signup_mode == "closed"`. Only reject non-admin first-logins in
closed mode. Keep the `open`/`approval` approval semantics unchanged for non-admins.

---

## FND-007 — Both OAuth providers can be disabled, locking everyone out

**Evidence:** `backend/app/routers/settings.py:35` `update_settings` accepts `allow_google=false` and
`allow_microsoft=false` together with no invariant. With both off, every provider login 403s at
`service.py:70` (`_assert_provider_enabled`); dev login is local-only, so a hosted instance becomes
unauthenticatable.

**Fix:** in `update_settings`, after merging the delta onto the settings object (but before commit),
reject the *resulting* state if it would leave zero enabled providers:
```python
if not (s.allow_google or s.allow_microsoft):
    raise ApiError(400, "NO_PROVIDER_ENABLED", "At least one sign-in provider must stay enabled")
```
Validate the merged result, not just the incoming fields (a request might disable only one while the other is already off).

---

## Constraints — do NOT change
- Reuse `ApiError` + the `{code,message}` envelope.
- Don't alter the immutable-subject identity keying or the approval-flow semantics for non-admins.
- FND-006: don't let a *non*-admin bypass closed mode.

## Acceptance criteria
- `docker compose exec api pytest` stays green (95), plus new tests:
  - FND-005: an admin can `POST /api/permissions` then create/patch a role including that new key (was 403, now 2xx).
  - FND-006: a first login by an `ADMIN_EMAILS` Google identity succeeds even with `signup_mode="closed"`; a non-admin first login still gets `SIGNUP_CLOSED`.
  - FND-007: `PUT /api/admin/settings` with both providers off → 400; disabling one while the other stays on → 200.
- `ruff check . && ruff format --check .` clean.
