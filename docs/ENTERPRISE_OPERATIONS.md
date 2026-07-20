# Enterprise operations contract

This repository contains the application and test controls. It intentionally does not configure
AWS resources, GitHub repository settings, or a deployment pipeline. Those external controls must
be implemented and evidenced by the environment owner before production release.

## Service objectives

Adopt explicit objectives before launch. A practical initial target is 99.9% monthly availability
for authenticated API requests, 99% of ordinary API responses below 500 ms, and 99% of identity
provider callbacks below 2 seconds, excluding a measured upstream provider outage. Alert on error
budget burn, readiness failure, authentication failure-rate changes, database saturation, nonce
issue/consume imbalance, session cleanup lag, migration mismatch, and audit-event loss.

Request IDs must be propagated through the edge, application logs, and any external telemetry.
Never log session cookies, provider tokens, authorization headers, secrets, or raw request bodies.

## Health semantics

- Liveness answers only whether the process can serve requests. It must not depend on MySQL or an
  identity provider.
- Readiness requires a database connection and the expected Alembic schema revision. An instance
  that is alive but not ready must receive no application traffic.
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

## Identity and session operations

- Review provider enabled/configured state, hosted domains, Microsoft tenants, bootstrap identities,
  and the signup policy before every production release.
- Keep dev authentication disabled outside isolated local development. Rotate its independent secret
  whenever exposure is suspected.
- Rotate the session signing key using an overlapping verification window once key-ring support is
  enabled; an emergency rotation may intentionally sign out every user.
- Revoke sessions and review admission policy after provider, tenant, allowlist, employee-status, or
  incident-driven access changes.
- Require recent strong authentication or the organization's IdP step-up policy before sensitive
  production administration. Application-only controls are not a substitute for Conditional Access
  or equivalent provider policy.

## Security and audit events

Access logs are operational telemetry, not an immutable audit system. Privileged changes and
authentication outcomes must be exported to restricted append-only storage or a SIEM with actor,
target, organization, outcome, request ID, source, timestamp, and a minimal before/after summary.
Monitor export loss and lag. Grant read access separately from application administration and test
tamper detection.

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
- External review/ruleset, secrets, deployment approval, rollback, backup, and monitoring controls
  are independently evidenced by their owners.
- All open risk acceptances have an owner and expiry date.
