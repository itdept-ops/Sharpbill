# ADR-001: One organization per deployment

- Status: Accepted
- Date: 2026-07-20
- Owners: Product and engineering

## Decision

Kingfisher is a single-tenant access-control console. One running application and its database
belong to exactly one organization. A shared database, shared application instance, or shared
identity namespace across customers is not supported.

Each organization must receive an isolated deployment, database, credentials, encryption keys,
identity-provider configuration, logs, backups, and recovery boundary. Operators must configure at
least one authoritative organization admission boundary (Google hosted-domain allowlist and/or
Microsoft tenant allowlist) before enabling self-service provisioning. Public provisioning without
an allowlist is an explicit, exceptional local/demo choice; it is not an enterprise production
mode.

## Why

The current schema intentionally treats users, roles, permissions, sessions, settings, and logs as
global data. Declaring that boundary makes the existing relational design coherent and prevents a
deployment from being mistaken for a multi-tenant SaaS control plane.

## Consequences

- Provider identities are unique within one organizational deployment.
- Backups, restores, retention, deletion, and incident response operate on one organization at a
  time.
- Cross-customer analytics or administration must aggregate sanitized external telemetry, not
  query another customer's application database.
- A request to host multiple organizations in one instance requires a new architecture decision
  and a schema redesign before implementation.

## Production acceptance checks

1. `APP_ENV=production`, secure cookies, and verified database TLS are mandatory startup guards.
2. At least one configured provider is enabled and its tenant/domain admission policy is reviewed.
3. The database name, runtime identity, secrets, backups, and log destination are unique to the
   organization.
4. A negative identity test proves an account outside the configured hosted domain or tenant is
   rejected before provisioning.
5. Restore testing proves an organization's backup cannot overwrite or be restored into another
   organization's live boundary without an explicit, reviewed recovery operation.

## Revisit trigger

Revisit this decision before any shared-customer deployment. A multi-tenant design must introduce
an organization root, tenant foreign keys and composite uniqueness constraints throughout the
schema, tenant-aware identity keys, mandatory scoped repositories, cross-tenant negative tests,
tenant-attributable audit events, and an independently reviewed isolation strategy.
