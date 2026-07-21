# ADR-002: ASP.NET Core backend architecture

- Status: Accepted
- Date: 2026-07-21
- Owners: Product and engineering

## Context

Sharpbill's original Python service concentrated HTTP routing, application policy, persistence,
background processing, and operational configuration in a single runtime. The product needs a
backend foundation that supports independently testable business behavior, explicit operational
boundaries, long-lived maintenance, and incremental ownership by multiple engineering teams.

This rewrite must preserve the existing `/api` contract and MySQL data while changing the runtime.
It must not require a flag-day schema reset, silently migrate on API startup, or weaken the access,
privacy, retention, and audit controls already represented by the product.

## Decision

The backend uses .NET 10 and ASP.NET Core with a layered architecture:

```text
HTTP -> Api -> Application -> Domain
              ^          ^
              |          |
       Infrastructure  Contracts
              ^
              |
           Workers
```

The diagram expresses allowed dependency direction, not runtime call direction. In particular:

- `Domain` contains business invariants and depends on no web, database, or provider package.
- `Application` coordinates use cases and declares interfaces for required external behavior.
- `Contracts` owns stable boundary models and does not expose persistence entities.
- `Infrastructure` implements application ports for MySQL, identity providers, tokens, time, and
  other external systems.
- `Workers` hosts bounded background services using the same application abstractions.
- `Api` is the composition root. It registers implementations, configures middleware and policies,
  maps transport contracts, and contains no persistence logic.

ASP.NET Core's built-in dependency-injection container is the only service locator. Constructor
injection is required. Application services and repositories expose focused interfaces; static
mutable state and resolving services from `IServiceProvider` inside business code are prohibited.
Service lifetimes must match their ownership: stateless coordinators are scoped or singleton only
when proven thread-safe, database units of work are scoped, and hosted workers create explicit
scopes per bounded iteration.

The HTTP pipeline centralizes cross-cutting behavior in ordered middleware and filters, including:

1. forwarded-header trust and correlation/request identity;
2. secure response headers and request-body limits;
3. exception mapping to RFC 9457 problem details without sensitive internals;
4. structured request/audit telemetry with bounded asynchronous delivery;
5. authentication, replay/session validation, authorization, and rate limiting;
6. endpoint execution and consistent response serialization.

Controllers or endpoint handlers remain thin. They validate transport input, call an application
service, and translate its typed result. Authorization uses named policies and resource checks;
roles are not scattered as string comparisons through controllers.

Configuration binds to validated options at startup. Secrets never have repository defaults or
appear in structured logs. Production startup fails closed when security-critical values are
missing or contradictory. Health is split between process liveness and traffic readiness; a live
process is not ready until database/schema and other required operational dimensions pass.

Persistence uses MySQL 8.4 through parameterized adapters. Existing schema version `0021` is the
bridge baseline. A dedicated `Sharpbill.Migrator` process owns reviewed, journaled schema changes;
the API never obtains migration authority and never migrates during startup. The migration tool
can baseline an empty database, validate an exact legacy database, and dry-run its plan. Deployment
automation must run one migrator before admitting traffic.

The delivery model uses multi-stage .NET 10 images. Development uses the SDK and `dotnet watch`.
Production uses the chiseled ASP.NET Core runtime, published artifacts, numeric identity
`65532:65532`, no shell or package manager, and a .NET-native health probe. The filesystem is
designed to run read-only except for a bounded `/tmp` mount. API and migrator targets are built
from the same reviewed source and dependency graph.

## Enforcement

- Nullable reference types, analyzers, deterministic builds, and warnings-as-errors are enabled
  repository-wide.
- Central package versions and NuGet transitive auditing constrain dependency changes.
- Architecture tests fail prohibited project and namespace dependencies.
- Unit tests exercise Domain and Application behavior without infrastructure.
- Integration tests exercise adapters and the HTTP pipeline against ephemeral MySQL.
- CI verifies formatting, builds Release artifacts, runs all tests with branch-aware coverage,
  audits packages, builds the exact production image, confirms its numeric user, smoke-tests
  liveness, scans it for vulnerabilities, and emits an SBOM.
- Frontend end-to-end tests continue to run against the Compose stack and existing API routes.

## Consequences

The additional projects and interfaces create more structure than a single web project, and every
new feature requires deliberate placement and mapping. That cost is accepted in exchange for
testable policy, replaceable adapters, enforceable dependency direction, and clearer operational
ownership.

The rewrite does not itself make Sharpbill multi-tenant, provide high availability, or approve a
production deployment. ADR-001's one-organization-per-deployment boundary remains authoritative.
The C# cutover is complete: existing data and HTTP compatibility were verified, and the Python
implementation is no longer part of the active runtime or release path. Frozen Alembic revisions
remain immutable, non-executable schema provenance.

## Alternatives considered

- **Single ASP.NET Core project:** less initial ceremony, but permits transport and persistence
  concerns to spread into business logic and is difficult to enforce as the team grows.
- **Service-per-feature microservices:** increases deployment, data consistency, observability, and
  incident-response complexity without a demonstrated scaling or ownership need.
- **Automatic migrations on API startup:** convenient locally but grants every replica schema
  authority and introduces races and uncontrolled deployment failure modes.
- **Retaining two production backends indefinitely:** lowers cutover urgency but doubles security,
  migration, and behavior-drift risk. Parallel operation is a bounded verification phase only.

## Revisit triggers

Revisit this decision before introducing multiple customer organizations per deployment, splitting
independently deployed services, changing the relational database, or replacing the built-in DI
container. Any proposal must identify the observed constraint, its operational cost, and how API,
data, authorization, audit, and rollback compatibility will be maintained.
