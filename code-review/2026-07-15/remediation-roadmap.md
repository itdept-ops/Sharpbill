# Sharpbill — Remediation Roadmap

**Audit:** 2026-07-15 · **Overall grade:** B− · **Findings:** 1 High, 26 Medium, 16 Low (grouped)
**Ordering principle:** risk × effort. Wave 0 is high-value / low-effort quick wins; later waves are grouped so related edits land in one session with the right tests.

> Rebranded archival copy: product labels were normalized to Sharpbill; findings and evidence remain tied to the 2026-07-15 review.

Each finding has a self-contained prompt in `fix-packs/`. IDs map 1:1 to the report's findings register.

---

## Wave 0 — Quick wins (high value, ≤30 min each, low blast radius)

These are near-mechanical, independently shippable, and each closes a real gap.

| ID | Fix | Effort | Why now |
|----|-----|--------|---------|
| **FND-001** | Bump `react-router-dom` → `6.30.4` (non-breaking) | XS | Clears the only production High CVE; one-line `package.json` + `npm install`. |
| **FND-007** | Reject a settings save that disables both providers | XS | Prevents a total auth lockout with a 3-line validator. |
| **FND-006** | Evaluate admin bootstrap before the closed-signup gate | XS | Prevents an admin-recovery lockout; reorder two blocks in `service.py`. |
| **FND-005** | Add the missing `if not _is_admin` bypass in `_guard_grantable` | XS | Restores the advertised "create + attach custom permission" flow. |
| **FND-028** | Fix the four documentation drifts | XS | Truth-in-docs; no code risk. |
| **FND-029** | Fix `online=false` no-op filter (`if online is not None`) + seed `last_seen_at` | S | Two real user-visible correctness bugs. |
| **FND-021** | Add security headers to `nginx.conf` | S | Closes clickjacking/nosniff/referrer gaps in one block. |
| **FND-043** | Consider defaulting `APP_ENV` to `production` (or require it explicitly) | XS | Removes a fail-open default. |

**Batches naturally:** FND-005 + FND-006 + FND-007 are all small `service.py`/`settings.py`/`roles.py` auth-logic edits → one session, one test file. FND-001 stands alone (dependency). FND-028 stands alone (docs).

---

## Wave 1 — RBAC & lockout hardening (the security core)

Do these together; they share `routers/users.py` + `routers/roles.py` and one new test module. **Ordering dependency:** land FND-003 (locking) before/with FND-002 (seniority) since both touch the same guards.

| ID | Fix | Effort |
|----|-----|--------|
| **FND-003** | Make last-admin count-and-mutate atomic (`SELECT … FOR UPDATE` / conditional UPDATE) | M |
| **FND-002** | Add target-seniority guard to update_status/update_role/bulk/kick | M |
| **FND-004** | Require actor to hold a role's *existing* perms before edit/delete | S |
| **FND-008** | Bootstrap Microsoft admin on immutable `oid`, not the email claim | M |
| **FND-030** | Revoke session rows on deactivate; document provider-toggle semantics | S |

**Acceptance:** a new `test_admin_protection.py` proving (a) a non-admin `presence.kick` holder cannot kick an admin, (b) two concurrent deactivations cannot reach zero admins, (c) a `roles.manage` delegate cannot strip a role above their privilege.

---

## Wave 2 — Reliability & correctness

| ID | Fix | Effort |
|----|-----|--------|
| **FND-016** | Offload WS DB work to `run_in_threadpool`; throttle inbound frames | M |
| **FND-017** | Catch `SQLAlchemyError` per item in `bulk_action` | S |
| **FND-018** | Cap WS connections; add idle-reaper/ping | M |
| **FND-025** | Add WebSocket auth/authz tests | M |
| **FND-026** | Add negative JWT tests + config-guard unit tests | S |
| **FND-035**/**FND-034** | Reconcile WS roster with `last_seen`; tighten count visibility & revocation lag | M |

**Batches:** FND-016/-018/-025/-034/-035 are all `ws.py` + a new `test_ws.py` → one focused session. FND-017/-026 are quick and independent.

---

## Wave 3 — Performance & data durability

| ID | Fix | Effort |
|----|-----|--------|
| **FND-013** | Async/batch the audit write; reuse decoded principal; add retention job | M |
| **FND-015** | Add index on `users.last_seen_at` (migration) | S |
| **FND-014** | Stream CSV with a row cap; throttle/cache analytics | M |
| **FND-039** | Drop the unused `identities` eager-load from the auth path | S |
| **FND-019** | Make migration 0002 downgrade safe for long role names | S |
| **FND-029** (clocks) | Pin DB session time zone to UTC | S |

**Batches:** FND-015 + FND-019 + the tz pin are all migration/DB work → one session. FND-013 + FND-014 are the write-path performance session.

---

## Wave 4 — Ops, supply chain & CI

| ID | Fix | Effort |
|----|-----|--------|
| **FND-020** | Patch backend deps; adopt hash-pinned lockfile | M |
| **FND-022** | Add `docker build --target prod` + `npm audit`/`pip-audit` gates to CI | M |
| **FND-023** | Non-root `USER` in both images; single-worker or shared state note | S |
| **FND-009** | Enable trusted `ProxyHeadersMiddleware`; document proxy requirement | M |
| **FND-010** | Gate `/api/docs` + `/api/openapi.json` (auth or non-local only) | S |
| **FND-040** | Pin base images; add `api` healthcheck; make cert fetch optional/hermetic | S |

**Note:** FND-009/-023 interact with deployment topology (kept AWS-agnostic per scope). CI gate (FND-022) would have caught FND-001 and FND-020 — do it early even though it's grouped here.

---

## Wave 5 — Accessibility & frontend polish

| ID | Fix | Effort |
|----|-----|--------|
| **FND-024** | Make the edit modal an accessible dialog (role, focus trap, Esc, focus mgmt) | M |
| **FND-027** | Explicit sign-in error/retry; WS reconnect with backoff | M |
| **FND-036** | Contrast floor on custom accent; fix `--muted` AA; label controls; route-change focus | M |
| **FND-041** | Refresh client permissions periodically / on focus | S |
| **FND-042** | Add one mutation round-trip e2e | S |

---

## Deferred / accept-with-eyes-open (Low, minimal current risk)

Track but don't rush; several are inherent to the "local Docker, single process" design and only matter on exposure/scale:

- **FND-031** (crypto niceties — replay-vs-leeway window, Google cert caching, nonce, secret entropy) — defense-in-depth; revisit when live OAuth + scale-out land.
- **FND-032** (schema `extra="forbid"`, list bounds, subject-id exposure) — hygiene.
- **FND-033** (rate-limit pruning / boundary 2× / no lockout) — moot once FND-009/-023 move limiting to a shared/edge layer.
- **FND-037** (422 body reflection, 500s not audited) — low info-disclosure / observability gap.
- **FND-038** (optimistic concurrency on admin edits) — rare in practice.

---

## Suggested execution order (single-threaded)

1. **Wave 0** (one afternoon — clears the High CVE and every lockout footgun).
2. **Wave 4 CI gate only** (FND-022) — so regressions in deps are caught before the rest of the work.
3. **Wave 1** (RBAC hardening) → **Wave 2** (reliability) → **Wave 3** (perf/data).
4. **Wave 4 remainder** (ops) → **Wave 5** (a11y/frontend).
5. Revisit the Deferred set.
