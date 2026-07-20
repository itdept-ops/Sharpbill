# Security policy

## Supported code

Security fixes are applied to the current `main` branch. This repository has no published stable
release line yet; do not treat an arbitrary commit or the reference files under `deploy/` as an
approved production release.

The checked-in controls and audit report are engineering evidence, not a penetration-test result,
legal opinion, or certification against SOC 2, ISO 27001, HIPAA, GDPR, or another compliance
framework. The application request log is bounded operational telemetry. Selected privileged and
authentication outcomes are also staged as append-only facts in a durable repository outbox with
separate delivery state. Neither path is an external immutable audit sink: the dispatcher,
restricted SIEM/WORM destination, its evidence-retention enforcement, and loss/lag monitoring remain
environment-owner controls. Application data follows `docs/DATA_RETENTION_PRIVACY.md`; see
`docs/ENTERPRISE_OPERATIONS.md` for the full boundary.

The 2026-07-20 owner decision explicitly defers live IdP-tenant exercises, AWS implementation,
GitHub governance settings, external observability/SIEM integration, production load targets, and
release signing/provenance integration. Repository tests and documentation must not be presented as
evidence that those controls exist.

## Reporting a vulnerability

Do not open a public issue containing exploit details, credentials, personal data, or tokens.
Contact the repository owner through a private, verified channel and include the affected commit,
reproduction conditions, impact, and a minimal proof of concept. The owner must acknowledge the
report, assign severity and an incident lead, preserve evidence, and coordinate disclosure only
after affected environments have been contained or patched.

## Repository safety rules

- Never commit `.env`, provider tokens, session cookies, private keys, database dumps, or customer
  data.
- Keep development authentication disabled by default and reachable only from an isolated local
  machine when explicitly enabled with its independent secret.
- Use unique least-privilege database identities. The API must never receive a database root
  password.
- Treat one application/database deployment as one organization, per
  `docs/architecture/ADR-001-single-tenant.md`.
- In production, configure a canonical HTTPS `PUBLIC_ORIGIN`, at least one effective identity
  provider, a reviewed database-backed `signup_mode`, and a reachable active administrator or valid
  immutable bootstrap path. `signup_mode` is the sole new-account admission policy; do not add an
  email-domain or provider-tenant allowlist as a second policy source.
- Production Google client IDs must use Google's OAuth web-client identifier form; Azure client IDs
  must be UUIDs. `TRUSTED_PROXY_IPS` accepts only explicit IP addresses/CIDR networks, never
  hostnames or wildcards, and production rejects world-wide proxy trust. The SPA receives only the
  effective providers' public client IDs at runtime from `/api/auth/config`.
- Keep provider verification and key-retrieval concurrency, timeout, cache/stale, document-size,
  and outage/unknown-key backoff bounds enabled. Treat repeated `PROVIDER_UNAVAILABLE` outcomes as
  an outage or abuse signal; do not increase the limits as an incident workaround.
- Migration `0017` persists only signature-verified Google `hd` and Microsoft `tid` context. These
  values support identity attribution and immutable bootstrap checks; they do not restrict ordinary
  onboarding by domain or directory. Do not forge or backfill them from email/UPN data.
- Treat `MYSQL_DATA_VOLUME` as a required, reviewed data target for the local Compose stack. Never
  let an image upgrade select or mutate an existing database volume implicitly.
- Grant `users.export` independently from `users.read`, and `security_events.view` independently
  from request-log `logs.view`. Grant `privacy.manage` only to personnel authorized to manage
  erasure, retention, and holds. Migration `0016` seeds the two export/evidence grants and migration
  `0019` seeds privacy administration only for the built-in admin role.
- Administrative clients must carry the latest returned `version`/`access_version` into role
  update/delete and user role/direct-grant requests. Treat `428 PRECONDITION_REQUIRED` and
  `409 STALE_WRITE` as refresh-and-review signals, never as permission to retry blindly.
- Monitor the protected access-log queue metrics, scheduled bounded request-log/session cleanup,
  privacy-lifecycle cleanup, hold state, and the security-event delivery backlog. Apply the approved
  defaults and evidence rules in `docs/DATA_RETENTION_PRIVACY.md`; do not describe a repository
  outbox as delivered, immutable, or independently retained evidence.
- Any populated upgrade that crosses `0013` must use the documented maintenance window, table-size
  and metadata-lock preflight, verified backup, and post-migration readiness gate in
  `docs/ENTERPRISE_OPERATIONS.md`; MySQL DDL auto-commits, so do not improvise downgrade/retry after
  a partial failure.
- Do not publish a release until the HTML audit report's unresolved external controls and accepted
  risks have named owners and expiry/verification dates.
- Treat erasure, location clearing, lifecycle cleanup, and hold changes as privileged, auditable
  operations. Never delete a provider identity binding in a way that silently reopens an erased or
  consumed bootstrap identity to self-provisioning.
- Review public claims at release time against the exact provider flows, migration head, database
  image, health semantics, test evidence, and deployment boundary. A passing CI workflow must not
  be described as production, compliance, recovery, or external-governance evidence.

## Dependency and image response

Automated scanning is a release gate, not a substitute for triage. Critical and high findings in a
reachable production path block release unless a documented, time-limited exception establishes
non-reachability, compensating controls, an owner, and a target fix date. Rebuild and rescan exact
image digests; retain the dependency lock, SBOM, scanner version, and result with release evidence.
