# How this was built — one operator, a fleet of adversarial agents

Kingfisher is an access-control console: verified SSO keyed to a provider's immutable identity,
database-backed RBAC enforced on every request, live presence, per-device sessions, a one-click
kill-switch, bounded access telemetry, and a durable security-event outbox. That's the *product*.
External outbox delivery and immutable retention remain environment controls. This is the more
interesting part — **how it was built.**

It was designed, implemented, hardened, and reviewed by one person orchestrating a fleet of AI
agents through a multi-agent workflow. Not "an AI wrote some code," but a real software development
lifecycle where models handled the grind — decomposition, parallel drafting, adversarial review,
test authoring, and live verification — while the operator steered intent and made the calls. The
repository history, tests, and CI workflow record that engineering process; they are not a
production assurance or compliance certification.

## The loop

Each substantial change ran through four phases, each a fan-out of agents:

1. **Understand / design.** For open-ended decisions (the visual direction, the session model),
   a *judged panel*: several agents each propose an independent approach, parallel judges score
   them on distinct axes, and the winner is synthesized — grafting the best ideas from the
   runners-up rather than picking one and hoping.
2. **Build.** Parallel section drafters produce coherent slices, integrated by hand. The operator
   owns the seams — where the pieces meet is where bugs hide.
3. **Adversarially review.** This is the part almost nobody shows. Independent agents are pointed
   at the diff with one instruction: *break it.* Each takes a different lens — RBAC, token
   verification, privacy, dead-code — and every finding is then handed to a **second** agent whose
   job is to *refute* it. Only findings that survive refutation get fixed. This kills the
   plausible-but-wrong "finding" that makes most AI review noise.
4. **Verify before commit.** Changes are expected to pass the relevant static, migration,
   integration, frontend, and browser checks before they are accepted. Playwright drives selected
   workflows over the local running stack; live provider tenants and external deployment controls
   require separate environment-level validation.

## The proof: real bugs the adversarial pass caught

An adversarial review is decoration unless it catches things that would have shipped. These are
real vulnerabilities the review agents found **in this codebase**, each fixed with a regression test
that still guards it:

- **Privilege-amplification via role assignment (HIGH).** A `roles.manage` delegate could mint a
  role carrying permissions it didn't itself hold, then assign it — escalating past its own
  authority. Fix: a "grant only what you hold" guard on role creation *and* assignment.
- **Puppet-admin via user management (HIGH).** A `users.manage` delegate could promote another
  account to full admin, and there was no last-admin protection. Fix: the amplification guard on
  role changes plus a last-active-admin lockout.
- **Settings escalation (HIGH).** A `settings.manage` delegate could set the *default sign-up role*
  to `admin` — every future signup would bootstrap as an administrator. Fix: the same amplification
  guard applied to the default-role setting.
- **Location leak through the kick endpoint (HIGH).** After opt-in GPS was gated to "self or
  `users.manage`", one path was missed: `POST /kick` is gated by `presence.kick` (a *distinct*
  permission) and still returned the target's coordinates. A `presence.kick`-only role — exactly the
  demo "Manager" — could kick a user and read their location. **Three independent review agents
  flagged this same hole**; the fix gates the kick response identically.
- **CSV formula injection.** Exported cells beginning with `= + - @` would execute as formulas when
  opened in a spreadsheet. Fix: neutralize them with a leading quote, tested against a
  `=HYPERLINK(...)` payload.
- **WebSocket re-auth gap.** The presence socket authenticated once at connect and never rechecked,
  so a kicked user's live connection lingered. Fix: re-authenticate on every wake and drop revoked
  sockets (close 1008).
- **LIKE-wildcard leakage & X-Forwarded-For spoofing.** Search terms didn't escape `%`/`_` (so
  `a_b` matched `axb`), and the request log trusted a caller-supplied `X-Forwarded-For` for the source
  IP. Both fixed and tested.
- **Sensitive-export overgrant.** Directory read access also authorized a bulk CSV extract, and
  request-log readers could inspect the higher-value durable security-event stream. Fix: migration
  `0016` introduces independent `users.export` and `security_events.view` grants, initially assigned
  only to the built-in admin role; both export paths self-audit.
- **Stale administrative overwrite.** Two administrators editing the same role or user access could
  silently replace each other's decision. Fix: version counters returned by reads are mandatory
  preconditions on role update/delete and user role/direct-grant changes; missing/stale writes fail
  with `428`/`409` and regression tests exercise the conflict.

None of these are hypothetical. Each was a working exploit against a real permission model, caught
before it shipped, and each has a test named after the failure it prevents.

## What verifies it

- **Backend:** the full HTTP stack under `pytest` against a real MySQL database, schema built by the
  actual Alembic migrations — an integration suite spanning auth, token replay, RBAC guards,
  per-device sessions, presence/kick, rate limiting, CSV-safety, location privacy, and request
  logging, plus access-log backpressure, scheduled bounded retention, optimistic write conflicts,
  least-privilege exports, strict production identity/proxy configuration, security-event outbox
  semantics, tenant-scoped Microsoft identity keys, database-controlled provider-wide onboarding,
  privacy lifecycle/hold/anonymization controls, and schema invariants through Alembic head `0019`.
- **Frontend:** Vitest + Testing Library over the code that gates access in the browser.
- **End-to-end:** a Playwright job boots the local stack (Vite + FastAPI + MySQL via Docker Compose)
  and checks, in a browser, that an admin can sign in and drive selected console workflows — and
  that a plain user is redirected away from the admin directory. This is representative application
  coverage, not a live-provider, penetration, load, recovery, or deployment test.

## Why it matters

The interesting claim isn't "AI can write a CRUD app." It's that a single operator, running an
adversarial multi-agent process with discipline, can produce software that is **tested, reviewed,
and honest about its boundary** — and that the process catches real security bugs a solo developer
under deadline would miss. The features here demonstrate the process; production approval still
depends on the external identity, infrastructure, recovery, monitoring, governance, and independent
security controls listed in the operations documentation.

*The best résumé is a running product.*
