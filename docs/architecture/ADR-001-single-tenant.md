# ADR-001: One organization per deployment

- Status: Accepted
- Date: 2026-07-20
- Owners: Product and engineering

## Decision

Sharpbill is a single-tenant access-control console. One running application and its database
belong to exactly one organization. A shared database, shared application instance, or shared
identity namespace across customers is not supported.

Each organization must receive an isolated deployment, database, credentials, encryption keys,
identity-provider configuration, logs, backups, and recovery boundary. Operators must configure at
least one identity provider. Identity-provider domain or directory membership is deliberately not
an application admission boundary: an isolated deployment may serve users from any verified Google
or Microsoft directory. The database-backed `site_settings.signup_mode` value is the sole policy
for creating new accounts.

## Repository enforcement

In production mode, application configuration requires at least one configured Google or Microsoft
provider, but does not require a hosted-domain or tenant allowlist. New-account admission is
authoritative in site settings:

- `open` creates any cryptographically verified identity from an enabled provider with the
  configured least-privilege default role;
- `approval` creates the account unapproved/pending until an authorized administrator approves it;
- `closed` rejects new accounts, except for an explicitly configured immutable bootstrap subject,
  while preserving sign-in for existing active and approved accounts.

Google's signed `hd` and Microsoft's signed `tid` remain identity/audit context rather than domain
admission filters. Microsoft identities are scoped by `(tid, oid)` because an `oid` is tenant-local;
Google identities remain scoped by their issuer subject. These application rules do not create a
tenant root in the schema or prove infrastructure isolation. The environment owner must still
supply a unique database, credentials, keys, logs, backup boundary, and network/deployment boundary
for each organization. Production rejects email-based administrator bootstrap; Microsoft
bootstrap still binds an immutable object ID to its explicitly configured tenant ID.

Traffic readiness is stricter than process startup: one configured provider must be enabled by site
settings (or the explicit local dev path must be usable), the default signup role must not be
`admin`, and either an active administrator must be reachable through an effective provider or a
valid immutable bootstrap path must remain. Public OAuth client IDs are delivered to the SPA at
runtime by `/api/auth/config`; environment variables, email domains, and tenant lists are not a
second source of onboarding policy.

Production configuration validates the Google web-client identifier form, canonical Azure UUIDs,
and explicit trusted-proxy IP/CIDR networks; hostnames, wildcards, and world-wide proxy trust cannot
silently widen the organization's boundary. Bulk directory extraction requires `users.export`
instead of `users.read`, and durable security evidence requires `security_events.view` instead of
request-log `logs.view`. These permissions are deployment-local authority and must not be reused as
a cross-organization evidence plane.

## Why

The current schema intentionally treats users, roles, permissions, sessions, settings, and logs as
global data. Declaring that boundary makes the existing relational design coherent and prevents a
deployment from being mistaken for a multi-tenant SaaS control plane.

## Consequences

- Provider identities are unique within one organizational deployment; Microsoft uniqueness is
  tenant-scoped so equal `oid` values from different directories cannot collide.
- Backups, restores, retention, deletion, and incident response operate on one organization at a
  time.
- The approved data lifecycle is defined in `docs/DATA_RETENTION_PRIVACY.md`; an environment's
  privacy owner may shorten a period, while extension requires a documented purpose and approval.
- Directory exports and durable security-event access are separately delegated and attributable
  within that organization. Role/access version preconditions reject stale administrative writes
  but do not replace the organization's approval and evidence process.
- Cross-customer analytics or administration must aggregate sanitized external telemetry, not
  query another customer's application database.
- A request to host multiple organizations in one instance requires a new architecture decision
  and a schema redesign before implementation.

## Production acceptance checks

1. `APP_ENV=production`, secure cookies, verified database TLS, and a canonical HTTPS
   `PUBLIC_ORIGIN` are mandatory startup guards.
2. At least one configured provider is enabled in site settings, and `signup_mode` is reviewed as
   the single new-account admission policy. No email-domain or provider-tenant allowlist is used.
3. An active administrator can authenticate through an effective provider or an approved immutable
   bootstrap path remains, and readiness reports the identity, administration, and admission
   dimensions as `ok`.
4. OAuth client IDs pass strict provider-specific validation, `/api/auth/config` exposes only
   effective providers, and every trusted-proxy entry is an explicitly reviewed IP/CIDR network.
5. `users.export`, `security_events.view`, and `privacy.manage` grants are reviewed independently
   from ordinary directory/request-log readers; administrative clients honor role/user access
   versions.
6. The database name, runtime identity, secrets, backups, and log destination are unique to the
   organization.
7. Admission tests prove that provider domain/tenant context does not override `signup_mode`: open
   admits, approval creates pending, and closed rejects an otherwise valid new identity.
8. Restore testing proves an organization's backup cannot overwrite or be restored into another
   organization's live boundary without an explicit, reviewed recovery operation.

## Revisit trigger

Revisit this decision before any shared-customer deployment. A multi-tenant design must introduce
an organization root, tenant foreign keys and composite uniqueness constraints throughout the
schema, tenant-aware identity keys, mandatory scoped repositories, cross-tenant negative tests,
tenant-attributable audit events, and an independently reviewed isolation strategy.
