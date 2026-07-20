# Security policy

## Supported code

Security fixes are applied to the current `main` branch. This repository has no published stable
release line yet; do not treat an arbitrary commit or the reference files under `deploy/` as an
approved production release.

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
- Do not publish a release until the HTML audit report's unresolved external controls and accepted
  risks have named owners and expiry/verification dates.

## Dependency and image response

Automated scanning is a release gate, not a substitute for triage. Critical and high findings in a
reachable production path block release unless a documented, time-limited exception establishes
non-reachability, compensating controls, an owner, and a target fix date. Rebuild and rescan exact
image digests; retain the dependency lock, SBOM, scanner version, and result with release evidence.
