# Sharpbill technology stack

This file is the canonical repository inventory for Sharpbill's current implementation stack.
It describes the checked-in application, local runtime, and CI evidence. It is not a production
deployment certification.

## Application

| Area | Current stack |
| --- | --- |
| Frontend | React 18.3.1, TypeScript 5.9, Vite 8.1, React Router 6.30 |
| Browser identity libraries | Google Identity Services loaded on demand; `@azure/msal-browser` 4.30 |
| Backend runtime | .NET 10 / ASP.NET Core targeting `net10.0` |
| Backend SDK/runtime pins | SDK `10.0.302`; ASP.NET Core runtime image `10.0.10-noble-chiseled-extra` |
| Persistence | MySQL 8.4.10 LTS, `utf8mb4`, Dapper 2.1, MySqlConnector 2.6 |
| Schema authority | `Sharpbill.Migrator`, a single-purpose C# executable |
| Historical schema provenance | Frozen Alembic revisions `0001` through `0021`; not active runtime code |
| Background work | Hosted .NET workers for retention and request-log buffering |
| Realtime | ASP.NET Core WebSockets for presence with HTTP polling fallback |

## Backend shape

The active backend is C# only. The layered solution is split into:

- `Sharpbill.Domain` for entities, value objects, and invariants.
- `Sharpbill.Contracts` for HTTP request/response contracts.
- `Sharpbill.Application` for use cases, policies, and service/repository ports.
- `Sharpbill.Infrastructure` for MySQL, identity-provider, token, clock, CSV, and telemetry adapters.
- `Sharpbill.Workers` for bounded background workloads.
- `Sharpbill.Api` for middleware, authentication, authorization, controllers, and DI composition.
- `Sharpbill.Migrator` for explicit database migration and schema validation.

ASP.NET Core middleware owns request context, security headers, CSRF checks, body limits,
request logging, rate limiting, and exception mapping. Controllers stay thin and call injected
services.

## Database and migration model

The compatibility baseline is exact schema `0021`. On an empty database, `Sharpbill.Migrator`
applies the reviewed `schema-0021.sql` snapshot, writes the `alembic_version=0021` marker, and
journals the C# baseline. On an existing database, it accepts only an exact legacy `0021` schema
after read-only structure and seed validation.

The ASP.NET Core API never mutates schema on startup. Databases below `0021` require an
operator-owned, source-matched archival migration artifact; the frozen Alembic files in this
repository are evidence, not a supported recovery toolchain.

## Local runtime

The supported repository-managed runtime is Docker Compose:

- `mysql`: digest-pinned MySQL 8.4.10, bound to loopback.
- `migrator`: explicit one-shot C# migrator profile.
- `api`: published ASP.NET Core production-shaped image, bound to loopback.
- `web`: Vite development server, bound to loopback.

Backend hot reload should use the direct SDK workflow with `dotnet watch`. The default Compose API
uses the published image to avoid Windows/Linux build-artifact drift.

## Container images

- Backend restore/build: digest-pinned `mcr.microsoft.com/dotnet/sdk:10.0.302-noble`.
- Backend production: digest-pinned `mcr.microsoft.com/dotnet/aspnet:10.0.10-noble-chiseled-extra`.
- Frontend build/dev: digest-pinned `node:24-alpine`.
- Frontend production reference: digest-pinned `nginx:1.31.3-alpine-slim`, running as user `101`.
- Edge reference: Caddy 2 reverse proxy in `deploy/`, not wired to a deploy pipeline.

Production-shaped images are release candidates only. Building them does not deploy the application
or supply external production controls.

## CI and repository controls

GitHub Actions runs four required protected-branch jobs:

- Frontend quality and tests.
- Backend quality and tests.
- End-to-end access control.
- Production images and supply chain.

The workflow uses pinned actions, read-only permissions, NuGet transitive vulnerability rejection,
npm audit, TypeScript/ESLint, Vitest, xUnit, migration dry-run/apply/validation, Playwright,
production image builds, SBOM upload, and blocking High/Critical Trivy scans.

The GitHub repository is private and `main` is protected. Changes land through pull requests with
required status checks.

## Production boundary

The repository does not implement AWS resources, managed database HA/PITR, centralized SIEM/WORM
delivery, production monitoring, deployment orchestration, rollback automation, or distributed
rate-limit/presence coordination. Those controls are production-owned and must be evidenced by the
environment owner before production use.
