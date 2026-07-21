# Sharpbill backend

Sharpbill's backend is an ASP.NET Core 10 application organized around explicit architectural
boundaries. The API is the HTTP composition root; business rules live outside controllers, and
infrastructure concerns are supplied through dependency injection.

The C# cutover is complete. The active backend, migrator, test, and container paths are .NET-only;
the frozen Alembic files are retained solely as non-executable schema provenance.

## Project layout

| Project | Responsibility |
| --- | --- |
| `Sharpbill.Domain` | Entities, value objects, and business invariants with no infrastructure dependencies |
| `Sharpbill.Contracts` | Stable HTTP/request/response contracts shared at system boundaries |
| `Sharpbill.Application` | Use cases and service/repository abstractions (`IService`-style ports) |
| `Sharpbill.Infrastructure` | MySQL, identity-provider, token, clock, and other adapter implementations |
| `Sharpbill.Workers` | Bounded background and retention workloads |
| `Sharpbill.Api` | ASP.NET Core middleware, authentication/authorization, endpoints, and DI composition |
| `Sharpbill.Migrator` | The single-purpose, fail-closed database migration executable |

The dependency direction and prohibited cross-layer references are enforced by
`Sharpbill.ArchitectureTests`. See
[`ADR-002`](../docs/architecture/ADR-002-dotnet-backend.md) for the governing decision.

## Prerequisites

- .NET SDK `10.0.302` (the repository's `global.json` selects the approved patch line)
- Docker Desktop with Compose v2 for the supported local stack
- MySQL 8.4 when running the API directly on the host

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
- `prod` contains only published API and migrator artifacts on the ASP.NET Core chiseled runtime.
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
