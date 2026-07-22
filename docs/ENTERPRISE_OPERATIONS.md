# Production operating boundary and control requirements

This repository contains the application and test controls. It intentionally does not configure
AWS resources, GitHub repository settings, or a deployment pipeline. Those external controls must
be implemented and evidenced by the environment owner before production release.

References to numbered migrations `0001`…`0021` below describe the frozen historical Alembic
provenance of the compatibility schema. Current schema authority belongs to the reviewed
`Sharpbill.Migrator` executable; the ASP.NET Core API never runs migrations at startup.
The C# cutover is complete: no Python service or migration image is built, published, or used by
the active release path.

## Tested operating boundary

The supported repository-managed runtime is the loopback-only local Compose stack:

- host ports bind to `127.0.0.1`; MySQL 8.4.10 is pinned by tag and digest;
- Compose fails closed until `MYSQL_DATA_VOLUME` names an explicitly selected fresh or
  restore-validated MySQL 8.4 volume;
- the production-shaped API image uses a digest-pinned .NET 10 ASP.NET Core chiseled runtime, while
  `global.json` pins the approved SDK patch; production images are still release candidates, not
  deployed artifacts;
- one application/database instance represents exactly one organization;
- the API receives an allowlisted environment and non-root database credentials, never the Compose
  root password;
- development authentication is disabled by default and requires local mode, an explicit flag,
  and a separate strong request secret;
- public OAuth client IDs and effective provider flags are served to the SPA at runtime by
  `/api/auth/config`, so an environment-specific client ID is not embedded in the static web image;
- production validates Google web-client-ID syntax, canonicalizes Azure client/tenant/object UUIDs,
  and limits `TRUSTED_PROXY_IPS` to explicit IP/CIDR entries without wildcard or world-wide trust;
- database `site_settings.signup_mode` is the only new-account admission policy; no environment
  email-domain or provider-tenant allowlist can silently override it;
- `users.export` is independent from directory read access, while `security_events.view` is
  independent from operational request-log access; migration `0016` initially grants both only to
  the built-in admin role;
- `privacy.manage` independently controls erasure, retention, and hold administration; migration
  `0019` initially grants it only to the built-in admin role;
- role update/delete and user role/direct-grant changes require the latest returned optimistic
  `version`/`access_version`, rejecting missing preconditions with `428` and stale writes with `409`;
- selected access telemetry is emitted synchronously to structured stdout and offered to a bounded,
  single-writer database queue with protected depth/drop/failure metrics;
- an interval worker independently performs approved privacy-lifecycle cleanup in bounded,
  separately committed batches, with delayed first execution and bounded shutdown;
- selected authentication and privileged outcomes are inserted as append-only event facts plus
  independent retry/lease state in a durable repository outbox;
- `/api/health/live` reports process liveness, while `/api/health/ready` requires MySQL, the exact
  `0021` compatibility baseline, an effective identity provider, a non-admin signup default role,
  and a reachable active administrator or valid bootstrap path; and
- rate limits, live presence, token-replay memory, and WebSocket broadcasts are process-local. The
  reference production topology therefore runs one API replica until those controls are externalized.

This boundary has no AWS resource implementation, edge TLS, HA, autoscaling, shared limiter/pub-sub,
managed secret rotation, PITR, external outbox dispatcher, immutable/WORM audit sink, SIEM
integration, production telemetry, or automated deployment and rollback. The files under `deploy/`
are unapproved references and do not close any of those gaps.

## External production gates

The following remain explicitly deferred to named environment owners; repository changes alone
cannot close them:

- GitHub rulesets/branch protection, required independent review, security-service enablement,
  environment approvals, and release governance;
- provider app-registration ownership, tenant consent, Conditional Access/step-up, lifecycle/SCIM
  integration where required, and live Google/Microsoft failover tests with non-production tenants;
- cloud/IaC ownership for edge TLS, private networking, workload identity, managed secrets and key
  rotation, resource limits, availability zones, deployment orchestration, and rollback;
- managed MySQL enforcement with separate runtime/migrator identities, server-side TLS policy,
  encryption, HA, PITR, retention, and independently timed restore drills;
- attributable delivery from the repository's transactional security-event outbox into a
  restricted append-only sink/SIEM, including dispatcher ownership, loss/lag monitoring, tamper
  detection, and legal retention;
- measured SLOs, load/failure tests, distributed rate-limit/presence coordination, client/runtime
  telemetry, on-call/incident exercises, independent penetration testing, jurisdiction-specific
  privacy review, and production-like accessibility validation; and
- externally operated artifact signing, provenance publication/verification, and release-policy
  enforcement.

The owner explicitly deferred these gates on 2026-07-20. Deferral is not acceptance for production;
it means repository work may continue while the associated audit findings remain deferred or
mitigated until an accountable external owner supplies evidence.

## Service objectives

Adopt explicit objectives before launch. The following is a candidate, not a currently measured or
contracted SLO: 99.9% monthly availability
for authenticated API requests, 99% of ordinary API responses below 500 ms, and 99% of identity
provider callbacks below 2 seconds, excluding a measured upstream provider outage. Alert on error
budget burn, readiness failure, authentication failure-rate changes, database saturation, nonce
issue/consume imbalance, session cleanup lag, migration mismatch, and audit-event loss.

The application generates the authoritative `X-Request-ID` for every request and propagates it
through responses, logs, and external telemetry. A valid caller-supplied `X-Request-ID` is retained
only as the distinct `client_request_id` field and returned as `X-Client-Request-ID`; never treat the
client value as unique or authoritative.
Never log session cookies, provider tokens, authorization headers, secrets, or raw request bodies.

## Health semantics

- Liveness answers only whether the process can serve requests. It must not depend on MySQL or an
  identity provider.
- Readiness requires a database connection, the exact `0021` schema compatibility baseline,
  and a usable identity/admission path. At least one configured provider must also be enabled in
  site settings (or the explicitly enabled local dev path must be usable), the signup default must
  not be the protected admin role, and either an active administrator must be reachable through an
  effective provider or a valid bootstrap path must remain. `signup_mode` may be open, approval, or
  closed; it is authoritative regardless of the verified account's email domain or provider tenant.
  An instance that is alive but not ready must receive no application traffic.
- Startup/migration checks are serial. Application replicas do not run schema changes concurrently.

## Database change procedure

1. Capture and verify a logical or physical backup before an engine or schema change.
2. Test the change against a clone with representative data and record elapsed time, locks, and
   query-plan changes.
3. Run exactly one `Sharpbill.Migrator` process with dedicated schema authority. On an empty
   database it applies the reviewed `0021` snapshot; on an existing database it accepts only an
   exact legacy `0021` schema, validates its structure and canonical seeds, and writes the C#
   baseline journal. It refuses partial Alembic history instead of guessing an upgrade path.
4. Verify database TLS, schema revision, constraints, case-sensitive opaque identifiers, and the
   critical application paths before directing traffic to the new version.
5. Recover a failed destructive change by restoring the verified backup into an isolated target,
   validating it, and then performing an explicitly approved cutover.

For the current baseline, dry-run, apply/bridge, and then validate explicitly:

```sh
docker compose run --rm migrator migrate --dry-run
docker compose run --rm migrator migrate
docker compose run --rm migrator validate
```

The API never migrates its database during startup. Future schema changes must be reviewed,
journaled migrations owned by `Sharpbill.Migrator` and follow expand/contract discipline.

### 0013 DDL maintenance window

Migration `0013` changes opaque-identifier collations, backfills and constrains session expiry,
changes `request_logs.id` from `INT` to `BIGINT`, and creates/replaces several indexes. MySQL 8.4
documents that a data-type change uses a table copy, and even online operations can wait for
exclusive metadata locks during their initial/final phases. Treat `0013` as potentially blocking
and rebuilding regardless of what a small development database did. Review the official
[online DDL operation matrix](https://dev.mysql.com/doc/refman/8.4/en/innodb-online-ddl-operations.html)
and [metadata-lock instrumentation](https://dev.mysql.com/doc/refman/8.4/en/performance-schema-metadata-locks-table.html)
for the exact target patch release.

This procedure is retained for a **legacy database below `0021`**. The active C# migrator does not
replay `0013` or any other partial Alembic history. The environment owner must supply an
operator-owned, explicitly approved, source-matched archival migration artifact whose provenance
and digest have been verified; this repository neither builds nor publishes that artifact. Use it
only to bring an isolated clone to exact `0021`, then cross the bridge with
`Sharpbill.Migrator validate` before admitting the ASP.NET Core API. If no approved artifact exists,
stop and establish a separately approved data-recovery plan; the provenance files are not a
runnable recovery package.

Capture the affected-table baseline from the application database before rehearsal and again before
the approved window (remember that `table_rows` is an InnoDB estimate):

```sql
SELECT table_name, table_rows, data_length, index_length,
       data_length + index_length AS total_bytes
FROM information_schema.tables
WHERE table_schema = DATABASE()
  AND table_name IN ('user_identities', 'login_nonces', 'site_settings', 'users',
                     'user_sessions', 'request_logs')
ORDER BY total_bytes DESC;
```

1. **Inventory and rehearse.** From an `0012` clone with representative cardinality and churn,
   capture row estimates plus data/index bytes for `user_identities`, `login_nonces`,
   `site_settings`, `users`, `user_sessions`, and `request_logs` from
   `information_schema.tables`. In the approved source-matched archival artifact, run
   `alembic upgrade 0013` while recording every statement's
   elapsed time, peak temporary/free-space use, DML blocking, metadata-lock wait, and replica lag if
   applicable; then run `alembic upgrade head`. Size the window and free space from that measured
   rehearsal, including table-copy/index workspace, not from row count alone.
2. **Back up and prove recovery.** Immediately before the window, quiesce a clone of the target and
   take the approved logical/physical backup. Verify its checksum and restore it into an isolated
   MySQL 8.4 target; record row counts/checksums, current revision, restore duration, owner, and the
   cutover/abort deadline. A backup that has not been restored is not the rollback plan.
3. **Drain writes and clear blockers.** Remove the application from traffic and stop every API
   process so request logging, sessions, and the scheduled retention worker cannot write. Confirm
   there are no long transactions in `information_schema.innodb_trx` and no unexpected granted or
   pending locks in `performance_schema.metadata_locks` / `sys.schema_table_lock_waits`. Establish a
   bounded metadata-lock wait/abort threshold and kill or resolve the owning application transaction
   before migration; do not let DDL queue indefinitely behind an unknown session.
4. **Run one archival migrator.** Verify the source is exactly `0012`, the approved artifact's
   frozen packaged history has
   one head, the migrator uses verified TLS and a dedicated identity, and the 0013 data preflight
   succeeds before its first DDL. Run `alembic upgrade 0013` from that source-matched artifact while
   watching process state, metadata locks, disk/temp capacity, database saturation, and the
   pre-agreed window. Keep traffic
   drained, then run `alembic upgrade head` to the exact packaged head. Never run migrations from
   multiple application replicas.
5. **Validate before admission.** Compare critical row counts/checksums to the pre-change record;
   inspect the expected collations, constraints, `BIGINT`, session expiry, and indexes; run
   the legacy `alembic current`, `alembic heads`, and `alembic check`, followed by
   `Sharpbill.Migrator migrate` and `Sharpbill.Migrator validate` to journal and verify the bridge;
   then start one API candidate and exercise
   authentication, session, RBAC, request-log, and export paths. Restore traffic only after
   `/api/health/ready` returns `200` with `database`, `schema`, `identity_provider`,
   `administration`, and `admission_policy` all `ok`.
6. **Abort safely.** MySQL DDL auto-commits statement by statement. If `0013` fails after its first
   DDL, preserve logs and schema evidence, keep traffic drained, and do not blindly rerun or
   downgrade while Alembic still records `0012`. Restore the verified backup into an isolated
   target, validate it, and perform the explicitly approved rollback/cutover.

The repository's MySQL 8.4 upgrade runbook is the authoritative local rehearsal procedure. Never
point an existing MySQL 8.0 data volume at a new image without a verified compatibility and restore
plan.

## Backup and restore evidence

The environment owner must define RPO and RTO, encrypted backup retention, immutable/cross-boundary
copies where required, key ownership, restore authorization, and legal holds. At least quarterly:

1. Select a backup without relying on the operator who created it.
2. Restore it into an isolated database with new credentials.
3. Run integrity checks, `Sharpbill.Migrator validate`, and representative application reads. For a
   pre-`0021` legacy restore, first use an operator-owned approved archival artifact as described
   above.
4. Measure achieved RPO/RTO and record every exception.
5. Destroy the isolated recovery environment using the approved data-handling process.

A successful backup job is not restore evidence.

### Local 2026-07-20 migration/recovery evidence

The audit workstation performed a local data-preserving rehearsal while moving the development
baseline from MySQL 8.0.46 to digest-pinned MySQL 8.4.10:

1. A logical `appdb` export and its checksum were verified in a separately retained pre-8.4 backup
   volume before engine cutover work continued.
2. The original pre-rebrand MySQL data directory subsequently underwent MySQL's one-way 8.0-to-8.4
   data-dictionary upgrade. It is retained as an isolated recovery artifact and must never be
   attached to MySQL 8.0.
3. The verified export was restored into a fresh MySQL 8.4 volume. Alembic advanced
   it from `0012` to `0013`; `current`, `heads`, and `alembic check` agreed at `0013` with no model
   drift.
4. Aggregate row counts across all application tables matched the pre-cutover source, and the
   full backend integration suite passed against an ephemeral database on the restored 8.4 service.
5. Before the remediation migrations, a second logical export was checksum-verified in a retained
   pre-0014 backup volume, restored into a disposable MySQL 8.4 instance, and
   verified at schema `0013` with 19 users, 19 identities, 27 sessions, and 1,782 request logs.
6. After applying `0014` through `0016`, the retained pre-0017 backup volume was independently
   checksum-verified and restore-tested at
   `0016` with the same four material row counts. Only its explicitly named disposable
   restore-check container and volume were removed afterward.
7. The live local volume then advanced to `0017`; `current`, `heads`, `alembic check`, all material
   row counts, the new authority columns, and every readiness dimension passed.
8. Before the next decision batch, a stopped cold snapshot was copied to a retained pre-0019 backup
   volume. All 200 files matched the source byte-for-byte by aggregate
   hash verification.
9. The live local volume advanced linearly from `0017` through `0018` and `0019`. Alembic
   current/head/drift checks passed, the API reported database, schema, identity-provider,
   administration, and admission-policy readiness as `ok`, and the rebuilt web service returned
   HTTP 200.
10. The additive `0020` legal-acceptance migration was then applied to the live local database.
    `alembic current` reported the single packaged head, `alembic check` reported no model drift,
    every readiness dimension remained `ok`, the versioned legal manifest matched the rebuilt web
    bundle, the login and legal routes returned HTTP 200, and the new evidence table began empty.
11. The additive `0021` migration then bound the exact acceptance statement, effective date, and
    per-document action semantics to legal evidence and added non-extending per-capture precise-
    location deadlines. Full ephemeral-MySQL migration tests passed before the live local volume
    advanced; this remains runtime validation rather than a new restore-test boundary.
12. The C# migration bridge subsequently validated and journaled the existing exact `0021` database,
    and independently applied the reviewed snapshot to an empty disposable MySQL database. The
    layered .NET solution's xUnit suites cover domain, application, architecture, migrator,
    service/API, identity, and repository boundaries; this remains repository evidence, not a
    production recovery drill.

Those retained recovery artifacts are local to that workstation and must be inventoried before cleanup.
This proves a local logical restore and application/schema validation only. It does **not** prove a
production RPO/RTO, encrypted or immutable retention, PITR, cross-boundary recovery, HA failover,
legal hold, or an independently operated restore drill.

These local recovery artifacts have an approved expiry of **2026-08-03**. On or after that date,
the workstation owner must remove each exact, inventoried artifact through a target-verified
disposal procedure unless a documented hold identifies the specific artifact. Record the operator,
artifact, time, and outcome; the application must never delete Docker volumes. Future production
backup copies have a proposed 35-day default expiry, pending external environment-owner approval
and implementation. See `docs/DATA_RETENTION_PRIVACY.md`.

The engine-cutover phase intentionally records the `0012` to `0013` state reached before later
remediation work. The subsequent local steps separately exercised migrations `0014` through the
then-current `0017` head with restore-tested boundaries, followed by a cold verified snapshot and
linear migration through the then-current head `0019`. The later additive `0020` and `0021` heads
received runtime validation but no new restore-test boundary. Neither that validation nor later
migration tests retroactively broadens this recovery rehearsal. Every target environment must
still repeat its own
normal head/readiness gates; this local evidence must not be presented as a production restore or
migration rehearsal.

## Identity and session operations

- Publish the API manifest and matching web legal content as one release. Treat every published
  bundle/document version as immutable, retain its approved rendered copy, and assign a new version
  for substantive changes. Missing, false, or stale acceptance must fail before provider work or
  session issuance; see `docs/LEGAL_DOCUMENTS.md` for the counsel and release gates.
- Review provider enabled/configured state, the runtime client IDs returned by `/api/auth/config`,
  immutable bootstrap identities, and database `signup_mode` before every production release.
- Production startup requires a canonical HTTPS `PUBLIC_ORIGIN` and at least one configured
  provider. Readiness then requires at least one configured provider to remain enabled in site
  settings. `signup_mode` is authoritative: open admits any verified new identity to the configured
  least-privilege default role, approval creates a pending account, and closed rejects new accounts
  except an explicitly configured immutable bootstrap subject. Do not implement an email-domain or
  provider-tenant onboarding allowlist.
- Production validates Google client IDs against the OAuth web-client identifier form and Azure
  client IDs as canonical UUIDs. Proxy trust accepts only explicit IP/CIDR peers; hostnames,
  wildcards, invalid networks, and world-wide production CIDRs are rejected. Keep
  `TRUSTED_PROXY_IPS` empty when there is no trusted proxy, and verify that the immediate socket peer
  is actually inside every configured network before accepting forwarded client/scheme data.
- Provider signature verification and key retrieval are fail-fast bounded. Keep the documented
  verification/network concurrency, connect/read timeout, key-document size, cache/stale, and
  outage/unknown-key backoff settings inside the API process and identity-provider budgets. Alert on
  `PROVIDER_UNAVAILABLE`; do not raise the limits to mask an outage or attacker-generated `kid`
  flood. Exercise planned key rotation and stale-cache expiry before each production release.
- Production startup rejects `ADMIN_EMAILS` and Azure admin object IDs without a configured admin
  tenant. Use immutable `GOOGLE_ADMIN_SUBJECTS` for Google bootstrap; Microsoft bootstrap requires
  the exact configured tenant plus immutable object ID. Keep every bootstrap identifier under
  separate review. This tenant binding is bootstrap authority, not an onboarding restriction.
- Migration `0017` persists the last signature-verified Google `hd` or Microsoft `tid` claim for
  each identity. Google `hd` is attribution context. Microsoft identity lookup and uniqueness use
  the tenant-scoped `(tid, oid)` namespace because `oid` alone is tenant-local. Administrative
  recovery counts a claimed bootstrap only while its owner remains active, approved, an
  administrator, and still matches its configured immutable bootstrap context. Do not bypass this
  by editing the identity row manually.
- Keep dev authentication disabled outside isolated local development. Rotate its independent secret
  whenever exposure is suspected.
- Rotate the session signing key by moving the old active value into
  `SESSION_JWT_PREVIOUS_SECRETS`, installing a new independent `SESSION_JWT_SECRET`, and retaining
  the old value only through the maximum session lifetime. Tokens carry a derived key ID plus an
  explicit issuer, audience, and token type. Remove expired overlap keys promptly; an emergency
  rotation may intentionally sign out every user.
- Set stable, deployment-specific `SESSION_JWT_ISSUER` and `SESSION_JWT_AUDIENCE` values. Treat a
  change as a session-invalidating migration and coordinate it with the overlap-key window.
- Review `MAX_ACTIVE_SESSIONS_PER_USER` (default 20) and `SESSION_RETENTION_DAYS` (default 30)
  against the organization's access and evidence policy. Login revokes the oldest over-cap live
  session. The interval worker independently prunes expired/old-revoked rows in bounded batches;
  monitor cleanup lag and do not treat session-row retention as security-event retention.
- Revoke sessions and review provider state, `signup_mode`, employee status, and bootstrap authority
  after directory or incident-driven access changes. Because domain membership is not an admission
  policy, offboarding remains an administrator/directory lifecycle responsibility.
- Require recent strong authentication or the organization's IdP step-up policy before sensitive
  production administration. Application-only controls are not a substitute for Conditional Access
  or equivalent provider policy.

## Authorization and concurrency operations

- `users.read` permits directory reads but not CSV extraction; `users.export` controls the bounded
  directory export. `logs.view` permits operational request-log reads/metrics but not durable
  security evidence; `security_events.view` controls both security-event reads and export. Both
  exports create a security event. `privacy.manage` separately controls retention/erasure/hold
  administration. Review these grants separately and remove them from broad support roles unless
  their duties require bulk data, evidence, or privacy authority.
- Migration `0016` creates those two built-in permissions and grants them only to the built-in admin
  role. Its upgrade refuses a conflicting custom permission with either reserved key, and its
  downgrade refuses to discard retained non-admin grants. Resolve either refusal intentionally;
  never rename or delete grants merely to force a migration through.
- Migration `0019` creates `privacy.manage`, adds account lifecycle timestamps and global hold state,
  and removes the redundant provider-email copy. Its downgrade refuses an active hold, retained
  lifecycle evidence, a missing/modified privacy permission, or non-admin/direct privacy grants.
  Resolve the state through the approved privacy workflow; never discard evidence to force a
  downgrade.
- Migration `0020` creates append-only legal-acceptance evidence with versioned, canonical
  document digests and adds its bounded, hold-aware retention path. Its downgrade refuses to
  discard retained acceptance records. Never
  delete those records merely to force rollback; follow the approved retention/hold process and
  retain the approved rendered bundle outside the mutable application release.
- Role update/delete must carry the `version` from the latest role read. User role assignment and
  direct-permission replacement must carry the latest `access_version`. Missing values return
  `428 PRECONDITION_REQUIRED`; mismatches return `409 STALE_WRITE`. Refresh, show the intervening
  state to the operator, re-authorize the intended delta, and submit a new decision. Automated
  clients must not convert either response into a blind retry.
- Lifecycle, role, grant, and singleton-policy check-and-act paths use explicit Dapper locking
  reads within one database transaction. Preserve the global lock order—`site_settings`, then
  role/permission, then user, then session—and keep authorization reads on the same transaction as
  their protected writes; otherwise MySQL `REPEATABLE READ` can observe stale policy or create a
  deadlock cycle. Independently committed bulk items reauthorize on every item.

## Scheduled retention operations

The approved data lifecycle is authoritative in `docs/DATA_RETENTION_PRIVACY.md`: exact GPS expires
after 24 hours; never-approved pending accounts after 30 days; sessions 30 days after expiry or
revocation; request logs after 90 days; verified erasure requests after a 30-day grace period;
disabled accounts after 365 days; repository security events plus delivery state after 400 days;
and versioned legal-acceptance evidence after a provisional 2,555 days. Active profile data lasts
for the account lifetime. Generated CSV responses are not retained as server-side files. Counsel
must approve the legal-evidence period and residual pseudonymous account link for each deployment.

The API's delayed interval worker enforces these schedules independently of new login or log
traffic. Each data class is processed in bounded, independently committed, idempotent batches.
The duration controls are `PRECISE_LOCATION_RETENTION_HOURS`,
`PENDING_ACCOUNT_RETENTION_DAYS`, `SESSION_RETENTION_DAYS`,
`REQUEST_LOG_RETENTION_DAYS`, `ACCOUNT_ERASURE_GRACE_DAYS`,
`DISABLED_ACCOUNT_RETENTION_DAYS`, `SECURITY_EVENT_RETENTION_DAYS`, and
`LEGAL_ACCEPTANCE_RETENTION_DAYS`. Configure their bounded
batch-size counterparts, `RETENTION_WORKER_INTERVAL_SECONDS`,
`RETENTION_WORKER_MAX_BATCHES_PER_CYCLE`, and the bounded shutdown timeout from measured volume/lock
behavior. Alert on cycle failure, oldest eligible record, backlog growth, and shutdown timeout; do
not increase batches until lock, redo, and replica-lag impact is measured.

After every cycle, Sharpbill takes one indexed MySQL backlog snapshot covering all seven retention
categories. A holder of `privacy.manage` can read the process-wide state at
`GET /api/admin/privacy/retention/metrics`: cycle progress and totals, consecutive failures, last
start/completion/full success/failure, failed categories, hold state, per-category changed/batch
counts, eligible backlog, and oldest eligible age. A `null` backlog timestamp means no successful
snapshot has occurred in this process; a stale timestamp means the last observation failed or the
worker stopped. The standard .NET `Sharpbill.Retention` meter emits cycle outcomes, changed rows,
category failures, hold state, backlog, oldest age, and last-success time for an attached collector.

At minimum, page the named operator when two scheduled intervals pass without a full success, a
cycle/category fails, or backlog/oldest age grows across consecutive cycles outside an approved
hold. Create policy-specific thresholds from measured volume rather than treating a nonzero due
count during a bounded cycle as an incident. The repository emits the endpoint, instruments, and
structured logs; collector configuration, durable metric storage, dashboards, paging delivery,
and alert ownership remain environment controls and must be proven before production approval.

Account lifecycle expiry performs privacy-safe anonymization rather than unsafe relational-root
deletion. It revokes sessions, removes profile/GPS/provider-email copies, legal-acceptance request
metadata, and direct grants; assigns the least-privilege role; and retains only the opaque provider
binding required to prevent silent reprovisioning or bootstrap reuse plus the time/version and
pseudonymous account link required by the provisional contract-evidence policy. Unless a hold
applies, users may clear exact and derived location
immediately and receive a 30-day erasure grace period. Privacy administration and hold changes
require dedicated authority and security-event evidence.

Authenticated users inspect policy/status and clear or schedule/cancel through `/api/privacy`.
Holders of `privacy.manage` inspect global state, update a referenced hold, and schedule/cancel a
verified target's request through `/api/admin/privacy`. A hold blocks deletion/scheduling with
`423 RETENTION_HOLD` but permits canceling an erasure request because cancellation preserves data.
Administrator accounts cannot be scheduled or automatically anonymized; transfer authority first.

A documented hold pauses governed application deletion but never extends anti-replay nonce state.
Review holds at least every 90 days. Repository security events expire at 400 days even when their
external delivery has not succeeded unless held; dependent delivery state is removed with the
event. An external dispatcher/SIEM/WORM sink may have its own approved evidence schedule and still
requires environment-owner implementation, loss/lag monitoring, tamper controls, and verified
deletion.

## Security and audit events

Access logs are operational telemetry, not an immutable audit system. The application emits their
structured representation synchronously, then uses a bounded single-writer queue for database
persistence; overload can produce explicit drops rather than unbounded request latency. Monitor the
protected queue depth, drop, error, and flush metrics and route stdout through an independently
operated collection path.

`GET /api/admin/logs/metrics` (permission `logs.view`) separates records rejected before admission
from accepted records lost after enqueue, reports outstanding accepted work, and timestamps the
last enqueue, persistence, drop, and write error. `loss_detected` remains true for the life of the
process after any loss, so alert on counter deltas/timestamps rather than repeatedly paging on the
boolean alone. The standard .NET `Sharpbill.RequestLogs` meter emits accepted, rejected, persisted,
post-enqueue-loss, and write-error outcomes plus queue depth/capacity, outstanding work, writer
state, and loss state. Page on any new loss/write error, a stopped writer while the API is serving,
or outstanding work that does not drain within the measured persistence objective. Collection,
durable storage, dashboards, alert delivery, and response ownership remain environment controls.

Request-log browsing is keyset-only: pass the returned `next_cursor` as `before_id`; nonzero
`offset` is rejected. Path search is an escaped prefix match rather than an unbounded contains
scan. Exact filtered `COUNT(*)` is omitted by default and returned only when an authorized caller
explicitly sends `include_total=true`; the admin UI does not request it and progressively loads
older rows. Keep exact counts for deliberate investigations, not polling. Profile the prefix and
other filters with production-shaped cardinality before adding an index or widening search
semantics; the frozen `0021` schema must not be changed without a reviewed forward migration.

Privileged changes and authentication outcomes are staged transactionally as append-only event
facts with separate mutable retry/lease state. The repository supplies bounded cursor reads,
self-audited CSV export, an approved 400-day application retention schedule, and worker-facing
claim/success/failure primitives. It
does **not** run an external dispatcher or provide immutable storage. An environment owner must
deliver the outbox to restricted append-only/WORM storage or a SIEM, monitor loss, lag, retries and
dead letters, apply the separately approved external-evidence retention/deletion schedule, grant
evidence access separately from application administration, and test tamper detection.

## Incident minimums

Every production environment needs a named on-call owner, severity matrix, communication channel,
provider and database escalation paths, evidence-preservation procedure, and post-incident review.
Run game days for identity-provider outage, database unavailability, compromised session key,
privileged-account misuse, migration failure, and restore. Track follow-up work to closure.

## Release evidence checklist

- All backend xUnit suites plus frontend and browser tests pass.
- Dependency, secret, static-analysis, and deployable-image scans meet the approved policy.
- The exact image digests and software bill of materials are retained.
- Readiness reports the expected schema and synthetic login/navigation checks pass.
- `Sharpbill.Migrator validate` confirms the exact `0021` schema and C# baseline journal, and the
  identity, administration, and admission readiness dimensions are all `ok`.
- Privacy lifecycle boundary/hold tests pass, no overdue unheld application record exceeds the
  approved schedule, and the local 2026-07-20 recovery artifacts are disposed by 2026-08-03 unless
  a specific documented hold applies.
- External review/ruleset, secrets, deployment approval, rollback, backup, and monitoring controls
  are independently evidenced by their owners.
- All open risk acceptances have an owner and expiry date.
- Public documentation and product copy match the exact database image, schema baseline/journal, effective
  provider paths, dev-auth gate, health semantics, test evidence, and deployment limitations.
