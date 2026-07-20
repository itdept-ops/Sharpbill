# Production operating boundary and control requirements

This repository contains the application and test controls. It intentionally does not configure
AWS resources, GitHub repository settings, or a deployment pipeline. Those external controls must
be implemented and evidenced by the environment owner before production release.

## Tested operating boundary

The supported repository-managed runtime is the loopback-only local Compose stack:

- host ports bind to `127.0.0.1`; MySQL 8.4.10 is pinned by tag and digest;
- Compose fails closed until `MYSQL_DATA_VOLUME` names an explicitly selected fresh or
  restore-validated MySQL 8.4 volume;
- the production-shaped API image uses a digest-pinned Wolfi base with exact Python 3.13 package
  versions; production images are still release candidates, not deployed artifacts;
- one application/database instance represents exactly one organization;
- the API receives an allowlisted environment and a non-root database URL, never the Compose root
  password;
- development authentication is disabled by default and requires local mode, an explicit flag,
  and a separate strong request secret;
- public OAuth client IDs and effective provider flags are served to the SPA at runtime by
  `/api/auth/config`, so an environment-specific client ID is not embedded in the static web image;
- production validates Google web-client-ID syntax, canonicalizes Azure client/tenant/object UUIDs,
  and limits `TRUSTED_PROXY_IPS` to explicit IP/CIDR entries without wildcard or world-wide trust;
- `users.export` is independent from directory read access, while `security_events.view` is
  independent from operational request-log access; migration `0016` initially grants both only to
  the built-in admin role;
- role update/delete and user role/direct-grant changes require the latest returned optimistic
  `version`/`access_version`, rejecting missing preconditions with `428` and stale writes with `409`;
- selected access telemetry is emitted synchronously to structured stdout and offered to a bounded,
  single-writer database queue with protected depth/drop/failure metrics;
- an interval worker independently prunes aged request logs and expired/revoked sessions in bounded,
  separately committed batches, with delayed first execution and bounded shutdown;
- selected authentication and privileged outcomes are inserted as append-only event facts plus
  independent retry/lease state in a durable repository outbox;
- `/api/health/live` reports process liveness, while `/api/health/ready` requires MySQL, the exact
  packaged Alembic head, an effective identity provider, a safe signup default, and a reachable
  active administrator or valid bootstrap path; and
- rate limits, live presence, token-replay memory, and WebSocket broadcasts are process-local. The
  production image therefore runs one API worker until those controls are externalized.

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
  detection, and legal retention; and
- measured SLOs, load/failure tests, distributed rate-limit/presence coordination, client/runtime
  telemetry, on-call/incident exercises, independent penetration testing, privacy review, and
  production-like accessibility validation.

## Service objectives

Adopt explicit objectives before launch. The following is a candidate, not a currently measured or
contracted SLO: 99.9% monthly availability
for authenticated API requests, 99% of ordinary API responses below 500 ms, and 99% of identity
provider callbacks below 2 seconds, excluding a measured upstream provider outage. Alert on error
budget burn, readiness failure, authentication failure-rate changes, database saturation, nonce
issue/consume imbalance, session cleanup lag, migration mismatch, and audit-event loss.

Request IDs must be propagated through the edge, application logs, and any external telemetry.
Never log session cookies, provider tokens, authorization headers, secrets, or raw request bodies.

## Health semantics

- Liveness answers only whether the process can serve requests. It must not depend on MySQL or an
  identity provider.
- Readiness requires a database connection, the exact Alembic head packaged with the running API,
  and a usable identity/admission path. At least one configured provider must also be enabled in
  site settings (or the explicitly enabled local dev path must be usable), the signup default must
  not be the protected admin role, and either an active administrator must be reachable through an
  effective provider or a valid bootstrap path must remain. An instance that is alive but not ready
  must receive no application traffic.
- Startup/migration checks are serial. Application replicas do not run schema changes concurrently.

## Database change procedure

1. Capture and verify a logical or physical backup before an engine or schema change.
2. Test the change against a clone with representative data and record elapsed time, locks, and
   query-plan changes.
3. Apply only a single linear Alembic head. Prefer expand/contract changes; do not rely on schema
   downgrade as the recovery plan for destructive data changes.
4. Verify database TLS, schema revision, constraints, case-sensitive opaque identifiers, and the
   critical application paths before directing traffic to the new version.
5. Recover a failed destructive change by restoring the verified backup into an isolated target,
   validating it, and then performing an explicitly approved cutover.

### 0013 DDL maintenance window

Migration `0013` changes opaque-identifier collations, backfills and constrains session expiry,
changes `request_logs.id` from `INT` to `BIGINT`, and creates/replaces several indexes. MySQL 8.4
documents that a data-type change uses a table copy, and even online operations can wait for
exclusive metadata locks during their initial/final phases. Treat `0013` as potentially blocking
and rebuilding regardless of what a small development database did. Review the official
[online DDL operation matrix](https://dev.mysql.com/doc/refman/8.4/en/innodb-online-ddl-operations.html)
and [metadata-lock instrumentation](https://dev.mysql.com/doc/refman/8.4/en/performance-schema-metadata-locks-table.html)
for the exact target patch release.

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
   `information_schema.tables`. Run `alembic upgrade 0013` there while recording every statement's
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
4. **Run one migrator.** Verify the source is exactly `0012`, the packaged history has one head, the
   migrator uses verified TLS and a dedicated identity, and the 0013 data preflight succeeds before
   its first DDL. Run `alembic upgrade 0013` from one migration process while watching process state,
   metadata locks, disk/temp capacity, database saturation, and the pre-agreed window. Keep traffic
   drained, then run `alembic upgrade head` to the current `0017` head. Never run migrations from
   multiple application replicas.
5. **Validate before admission.** Compare critical row counts/checksums to the pre-change record;
   inspect the expected collations, constraints, `BIGINT`, session expiry, and indexes; run
   `alembic current`, `alembic heads`, and `alembic check`; then start one API candidate and exercise
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
3. Run integrity checks, Alembic revision checks, and representative application reads.
4. Measure achieved RPO/RTO and record every exception.
5. Destroy the isolated recovery environment using the approved data-handling process.

A successful backup job is not restore evidence.

### Local 2026-07-20 migration/recovery evidence

The audit workstation performed a local data-preserving rehearsal while moving the development
baseline from MySQL 8.0.46 to digest-pinned MySQL 8.4.10:

1. A logical `appdb` export and its checksum were verified in the separately retained local volume
   `kingfisher_pre84_backup_20260720` before engine cutover work continued.
2. The original `kingfisher_mysql_data` directory subsequently underwent MySQL's one-way 8.0-to-8.4
   data-dictionary upgrade. It is retained as an isolated recovery artifact and must never be
   attached to MySQL 8.0.
3. The verified export was restored into a fresh `kingfisher_mysql84_data` volume. Alembic advanced
   it from `0012` to `0013`; `current`, `heads`, and `alembic check` agreed at `0013` with no model
   drift.
4. Aggregate row counts across all application tables matched the pre-cutover source, and the
   full backend integration suite passed against an ephemeral database on the restored 8.4 service.
5. Before the remediation migrations, a second logical export was checksum-verified in retained
   volume `kingfisher_pre0014_backup_20260720`, restored into a disposable MySQL 8.4 instance, and
   verified at schema `0013` with 19 users, 19 identities, 27 sessions, and 1,782 request logs.
6. After applying `0014` through `0016`, retained volume
   `kingfisher_pre0017_backup_20260720` was independently checksum-verified and restore-tested at
   `0016` with the same four material row counts. Only its explicitly named disposable
   restore-check container and volume were removed afterward.
7. The live local volume then advanced to `0017`; `current`, `heads`, `alembic check`, all material
   row counts, the new authority columns, and every readiness dimension passed.

Those names and artifacts are local to that workstation and must be inventoried before cleanup.
This proves a local logical restore and application/schema validation only. It does **not** prove a
production RPO/RTO, encrypted or immutable retention, PITR, cross-boundary recovery, HA failover,
legal hold, or an independently operated restore drill.

The engine-cutover phase intentionally records the `0012`→`0013` state reached before later
remediation work. The subsequent local steps separately exercised migrations `0014` through the
repository's current linear head `0017`, with a new restore-tested boundary before each migration
batch. Every target environment must still repeat its own normal head/readiness gates; this local
evidence must not be presented as a production restore or migration rehearsal.

## Identity and session operations

- Review provider enabled/configured state, the runtime client IDs returned by `/api/auth/config`,
  hosted domains, the single admitted Microsoft tenant, bootstrap identities, and the signup policy
  before every production release.
- Production startup requires a canonical HTTPS `PUBLIC_ORIGIN`, closed public signup, and at least
  one configured provider. Google requires a signed-hosted-domain admission allowlist. If Microsoft
  is configured, `ALLOWED_AZURE_TENANTS` must contain exactly one tenant. Readiness then requires at
  least one of those configured providers to remain enabled in site settings.
- Production validates Google client IDs against the OAuth web-client identifier form and Azure
  client IDs as canonical UUIDs. Proxy trust accepts only explicit IP/CIDR peers; hostnames,
  wildcards, invalid networks, and world-wide production CIDRs are rejected. Keep
  `TRUSTED_PROXY_IPS` empty when there is no trusted proxy, and verify that the immediate socket peer
  is actually inside every configured network before accepting forwarded client/scheme data.
- Provider signature verification and key retrieval are fail-fast bounded. Keep the documented
  verification/network concurrency, connect/read timeout, key-document size, cache/stale, and
  outage/unknown-key backoff settings inside the API worker and identity-provider budgets. Alert on
  `PROVIDER_UNAVAILABLE`; do not raise the limits to mask an outage or attacker-generated `kid`
  flood. Exercise planned key rotation and stale-cache expiry before each production release.
- Production startup rejects `ADMIN_EMAILS`, Azure admin object IDs without a configured admin
  tenant, and an Azure admin tenant outside `ALLOWED_AZURE_TENANTS`. Use immutable
  `GOOGLE_ADMIN_SUBJECTS` for hosted Google bootstrap and keep every bootstrap identifier under
  separate review.
- Migration `0017` persists the last signature-verified Google `hd` or Microsoft `tid` claim for
  each identity. Administrative recovery counts a claimed bootstrap only while its owner remains
  active, approved, an administrator, and admitted by the current organization policy. Legacy
  claimed identities with no persisted authority fail readiness closed until a successful provider
  login refreshes that evidence; do not bypass this by editing the identity row manually.
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
- Revoke sessions and review admission policy after provider, tenant, allowlist, employee-status, or
  incident-driven access changes.
- Require recent strong authentication or the organization's IdP step-up policy before sensitive
  production administration. Application-only controls are not a substitute for Conditional Access
  or equivalent provider policy.

## Authorization and concurrency operations

- `users.read` permits directory reads but not CSV extraction; `users.export` controls the bounded
  directory export. `logs.view` permits operational request-log reads/metrics but not durable
  security evidence; `security_events.view` controls both security-event reads and export. Both
  exports create a security event. Review these grants separately and remove them from broad support
  roles unless their duties require bulk data/evidence access.
- Migration `0016` creates those two built-in permissions and grants them only to the built-in admin
  role. Its upgrade refuses a conflicting custom permission with either reserved key, and its
  downgrade refuses to discard retained non-admin grants. Resolve either refusal intentionally;
  never rename or delete grants merely to force a migration through.
- Role update/delete must carry the `version` from the latest role read. User role assignment and
  direct-permission replacement must carry the latest `access_version`. Missing values return
  `428 PRECONDITION_REQUIRED`; mismatches return `409 STALE_WRITE`. Refresh, show the intervening
  state to the operator, re-authorize the intended delta, and submit a new decision. Automated
  clients must not convert either response into a blind retry.

## Scheduled retention operations

The API starts a delayed interval worker so cleanup does not depend on a new login or request-log
write. By default, every 3,600 seconds it commits up to ten independent batches per table: 2,000
request logs per batch older than 90 days and 500 expired/old-revoked sessions per batch older than
30 days. Thus one default cycle deletes at most 20,000 request logs and 5,000 sessions. Configure
`REQUEST_LOG_RETENTION_DAYS`, `SESSION_RETENTION_DAYS`, both batch sizes,
`RETENTION_WORKER_INTERVAL_SECONDS`, `RETENTION_WORKER_MAX_BATCHES_PER_CYCLE`, and the bounded
shutdown timeout from measured volume/lock behavior. Alert on cycle failure, backlog growth, and a
shutdown timeout; do not increase batches until lock, redo, and replica-lag impact is measured.

`SECURITY_EVENT_RETENTION_DAYS` sets each event's `retention_until` intent. The repository worker
does not delete security events or delivery rows. External dispatcher/SIEM/WORM retention,
legal-hold, verified deletion, loss/lag, and dead-letter operations remain environment-owner work.

## Security and audit events

Access logs are operational telemetry, not an immutable audit system. The application emits their
structured representation synchronously, then uses a bounded single-writer queue for database
persistence; overload can produce explicit drops rather than unbounded request latency. Monitor the
protected queue depth, drop, error, and flush metrics and route stdout through an independently
operated collection path.

Privileged changes and authentication outcomes are staged transactionally as append-only event
facts with separate mutable retry/lease state. The repository supplies bounded cursor reads,
self-audited CSV export, retention intent, and worker-facing claim/success/failure primitives. It
does **not** run an external dispatcher or provide immutable storage. An environment owner must
deliver the outbox to restricted append-only/WORM storage or a SIEM, monitor loss, lag, retries and
dead letters, enforce legal retention/deletion, grant evidence access separately from application
administration, and test tamper detection.

## Incident minimums

Every production environment needs a named on-call owner, severity matrix, communication channel,
provider and database escalation paths, evidence-preservation procedure, and post-incident review.
Run game days for identity-provider outage, database unavailability, compromised session key,
privileged-account misuse, migration failure, and restore. Track follow-up work to closure.

## Release evidence checklist

- Application unit, integration, migration, frontend, accessibility, and browser tests pass.
- Dependency, secret, static-analysis, and deployable-image scans meet the approved policy.
- The exact image digests and software bill of materials are retained.
- Readiness reports the expected schema and synthetic login/navigation checks pass.
- The deployed database reports the current single Alembic head (`0017` for this revision), and
  the identity, administration, and admission readiness dimensions are all `ok`.
- External review/ruleset, secrets, deployment approval, rollback, backup, and monitoring controls
  are independently evidenced by their owners.
- All open risk acceptances have an owner and expiry date.
- Public documentation and product copy match the exact database image, migration head, effective
  provider paths, dev-auth gate, health semantics, test evidence, and deployment limitations.
