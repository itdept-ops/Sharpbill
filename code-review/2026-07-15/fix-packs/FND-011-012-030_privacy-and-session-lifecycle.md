# Fix Pack — FND-011 / FND-012 / FND-030: Privacy exposure + session lifecycle

**Severity:** Medium (FND-011, FND-012), Low (FND-030) · **Domain:** Security: session & privacy · **Effort:** S

## Context
`[repository root]`. The app has a deliberate location-privacy model: precise GPS
(`last_latitude/longitude/accuracy/at`) is shown only to the user themselves or a `users.manage` holder,
via the `include_location` flag in `UserOut.from_user` (`backend/app/schemas/user.py:47-86`). Two paths
leak location-adjacent data around that control, and deactivation leaves stale session rows.

---

## FND-011 — Session IPs & user-agents exposed to any `users.read` holder
**Evidence:** `backend/app/routers/users.py:362` `list_user_sessions` is gated on `users.read` and returns
every session row's `ip` + `user_agent` (`SessionOut`, `backend/app/schemas/auth.py:33-50`). The app gates
precise GPS behind the stricter `users.manage`/self, but hands per-device IPs + device fingerprints to the
broader `users.read` audience — an inconsistent privacy model.

**Fix:** gate other users' session IP/UA behind `users.manage` (or self) to match the GPS rule. Options:
(a) change the `list_user_sessions` gate to `users.manage`; or (b) keep `users.read` but null/mask `ip`
(and optionally coarsen `user_agent`) in `SessionOut` for viewers who lack `users.manage` and aren't the
owner. Prefer (b) so the read stays useful. The self-service route (`/api/auth/sessions`) must keep showing
the user their own IPs.

---

## FND-012 — GPS-derived location & timezone bypass `include_location`
**Evidence:** `backend/app/routers/auth.py:136-147` stores raw GPS **and** reverse-geocodes it into
`user.location` / `user.timezone`. `UserOut.from_user` strips lat/long when `include_location=False`
(`schemas/user.py:82-85`) but `location`/`timezone` are always serialized — and included in CSV export
(`users.py:139-153`). So the city/region derived from opt-in GPS leaks to every `users.read` viewer.
Note: these fields are also user-editable via profile, so they aren't *always* GPS-derived.

**Fix (pick one):**
- **Separate the fields:** store the GPS-derived label in a distinct column (e.g. `derived_location`,
  `derived_timezone`) gated by `include_location`, and keep the user-typed `location`/`timezone` freely
  visible. Cleanest, but needs a migration.
- **Or gate the existing fields** with the same `include_location` rule (accepting that a user-typed
  location also becomes gated). Simpler, no migration.
Choose based on product intent; document the choice.

---

## FND-030 — Deactivation leaves phantom "active" session rows
**Evidence:** `backend/app/routers/users.py:313-329` `update_status` sets `session_valid_after` (the kill
epoch) on deactivate but does **not** call `revoke_all_for_user` (only `kick_user` at :353 does). The
epoch blocks token *use*, but the session rows keep `revoked_at IS NULL` and still appear in the sessions
API as active. Also, disabling a provider / tightening the allowlist (`service.py:70`, `:58`) is enforced
only at next login and never revokes existing sessions — so those toggles aren't the real-time kill
switch they appear to be.

**Fix:** in `update_status` (and the bulk `deactivate` branch, `users.py:189-195`), call
`revoke_all_for_user(db, user.id)` after setting the epoch, mirroring `kick_user`. Document (README security
section) that provider-disable/allowlist changes apply at next authentication, not retroactively — or, if
retroactive revocation is desired, add an explicit "sign out affected users" action.

## Constraints — do NOT change
- Keep the self-service `/api/auth/sessions` showing the owner their own IPs (FND-011).
- Keep the immutable-subject identity model; don't weaken the existing GPS gating.
- Reuse `revoke_all_for_user` (`backend/app/auth/sessions.py:46`).

## Acceptance criteria
- FND-011: a `users.read`-only viewer gets masked/absent `ip` for another user's sessions; a `users.manage`
  holder and the owner still see it. Test in `backend/tests/test_sessions.py`.
- FND-012: the chosen approach verified by a test — a `users.read`-only viewer cannot see another user's
  GPS-derived location/timezone (or the fields are separated), and CSV export respects it.
- FND-030: after `PATCH /api/users/{id}/status {is_active:false}`, that user's session rows are revoked
  (`GET /api/users/{id}/sessions` returns none active). Suite green; `ruff` clean.
