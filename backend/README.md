# Sharpbill backend

Sharpbill's backend is an ASP.NET Core 10 application organized around explicit architectural
boundaries. The API is the HTTP composition root; business rules live outside controllers, and
infrastructure concerns are supplied through dependency injection.

The C# cutover is complete. The active backend, migrator, test, and container paths are .NET-only;
the frozen Alembic files are retained solely as non-executable schema provenance.

See [`../docs/TECH_STACK.md`](../docs/TECH_STACK.md) for the repository-wide technology inventory.

## Stack snapshot

- Target framework: `net10.0`
- Approved SDK patch: `10.0.302`
- Production runtime image: ASP.NET Core `10.0.10-noble-chiseled-extra`
- Persistence: MySQL 8.4 LTS through Dapper and MySqlConnector
- Schema authority: C# `Sharpbill.Migrator` at compatibility baseline `0021`
- Background work: hosted .NET workers for retention and request-log persistence
- API surface: ASP.NET Core middleware, authentication/authorization, controllers, and DI

## Project layout

| Project | Responsibility |
| --- | --- |
| `Sharpbill.Domain` | Entities, value objects, and business invariants with no infrastructure dependencies |
| `Sharpbill.Contracts` | Stable HTTP/request/response contracts shared at system boundaries |
| `Sharpbill.Application` | User use cases, cross-provider authentication policy/admission, mappings, export primitives, and service/repository/transaction ports |
| `Sharpbill.Infrastructure` | MySQL transaction/repository, identity-provider, token/session, request-context, telemetry, and other runtime adapters |
| `Sharpbill.Workers` | Bounded background and retention workloads |
| `Sharpbill.Api` | ASP.NET Core middleware, authentication/authorization, endpoints, and DI composition |
| `Sharpbill.Migrator` | The single-purpose, fail-closed database migration executable |

The dependency direction and prohibited cross-layer references are enforced by
`Sharpbill.ArchitectureTests`. See
[`ADR-002`](../docs/architecture/ADR-002-dotnet-backend.md) for the governing decision.

## Service boundaries

Controllers are transport adapters. They map HTTP contracts and call injected service interfaces;
an architecture test rejects controller constructor dependencies on repositories, units of work,
database sessions, connection factories, and database connection or transaction types. Route-level
self/administrator guards and development-authentication validation therefore execute in services,
before protected persistence work begins.

The public `IUserService` contract is preserved by a small Application-owned facade that delegates
to focused query, profile, access, and lifecycle use cases. Those use cases depend on Application
ports such as `ITransactionExecutor`, repositories, `IClock`, and `IRequestContextAccessor`; the
MySQL transaction executor is the sole runtime implementation. `IAuthService` similarly delegates
to configuration, external-login, development-login, account, and session-operation services.
Authentication policy, admission, identity mapping, and security-event construction live in
Application and receive a secret-free options projection; provider verification and transactional
login/session coordination remain Infrastructure adapters.

`RequestContextMiddleware` creates one canonical request context per request. Controllers and
services consume that same value through `IRequestContextAccessor` rather than reconstructing it.
Business and query cutoffs use `IClock`, which keeps time-sensitive behavior deterministic in tests.
Architecture tests pin both facade dependency sets, enforce ownership of the user/authentication
Application code, and prevent runtime types from leaking into user use-case constructors.

## Prerequisites

- .NET SDK `10.0.302` (the repository's `global.json` selects the approved patch line)
- Docker Desktop with Compose v2 for the supported local stack
- MySQL 8.4 LTS when running the API directly on the host

## Supported local workflow

Create `.env` from the repository-level `.env.example`, choose a fresh or restore-validated
`MYSQL_DATA_VOLUME`, and provide strong development secrets. Then run:

```sh
docker compose up -d mysql
docker compose run --rm migrator migrate
docker compose up --build api web
```

The API does not migrate its database during startup. This is intentional: schema authority is a
separate operational action, and only one migrator should run for a deployment. Validate an
already-migrated database without changing it with:

```sh
docker compose run --rm migrator validate
```

For local UI/E2E fixtures only, `docker compose run --rm migrator seed-demo` idempotently creates
the reviewed demo records after validating the schema. The command is rejected unless
`APP_ENV=local` or the operator makes the exceptional opt-in
`SHARPBILL_ALLOW_DEMO_SEED=true`; never enable it in a real environment.

The development API listens on `http://127.0.0.1:8000` by default. Its process-liveness endpoint
is `/api/health/live`; `/api/health/ready` is the traffic-admission check and includes database and
schema validation.

## Run directly with the SDK

Set the `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, and `DB_PASSWORD` variables (or the mutually
exclusive `ConnectionStrings__Sharpbill`, including `SslMode=VerifyFull;SslCa=...` in production)
plus the required authentication settings, then run:

```sh
dotnet restore Sharpbill.slnx
dotnet run --project src/Sharpbill.Migrator -- migrate
dotnet watch --project src/Sharpbill.Api run --no-launch-profile --urls http://0.0.0.0:8000
```

Configuration is provided through ASP.NET Core's normal providers. Nested option names use double
underscores in environment variables. Legacy uppercase settings that are part of Sharpbill's
deployment contract remain explicitly mapped and validated; unknown environment variables are not
treated as application configuration.

## Quality gates

Run the same core checks used by CI from this directory:

```sh
dotnet restore Sharpbill.slnx
dotnet format Sharpbill.slnx --verify-no-changes --no-restore
dotnet build Sharpbill.slnx --configuration Release --no-restore
dotnet test Sharpbill.slnx --configuration Release --no-build \
  --collect:"XPlat Code Coverage" --results-directory TestResults
dotnet package list --project Sharpbill.slnx --vulnerable --include-transitive --no-restore
```

Warnings and analyzer findings fail builds. NuGet auditing includes transitive packages at
`moderate` severity or higher. CI uploads Cobertura coverage results for review.

## Container targets

- `dev` is an opt-in .NET SDK image with `dotnet watch` for container-specific development.
- `prod` contains only published API and migrator artifacts on the digest-pinned ASP.NET Core
  chiseled runtime.
- `migrator` uses the same immutable artifacts and runtime but starts the migration executable.

The supported repository-level Compose stack runs the API from `prod`, without a host source bind
mount. This keeps Windows and Linux build artifacts isolated; use the direct SDK workflow above
when backend hot reload is required.

The production process runs as numeric user and group `65532:65532`, writes temporary framework
state only beneath `/tmp`, and does not require a package manager, shell, `curl`, or `wget`.
Production mounts should make the root filesystem read-only and provide a bounded `tmpfs` for
`/tmp`. The container health probe is implemented by the published .NET executable so those
hardening properties do not depend on adding diagnostic tools to the image.

Build the production targets with:

```sh
docker build --target prod -t sharpbill-api:local .
docker build --target migrator -t sharpbill-migrator:local .
```

Image creation is not deployment approval. Production still requires externally managed secrets,
TLS, backups/PITR, observable logs, admission checks against `/api/health/ready`, and the controls
described in the repository's enterprise operations documentation.
