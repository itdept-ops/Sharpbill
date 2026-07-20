# deploy/ — production reference (NOT wired up)

These files sketch a possible single-host production topology. They are **not an approved or
tested production deployment**. AWS resources are not configured by this repository, nothing here
runs automatically, and the CI workflow has no deploy job. The environment owner must provide and
evidence the external controls in `../docs/ENTERPRISE_OPERATIONS.md` before production use.

- `docker-compose.prod.yml` — Caddy (TLS) + api + web (nginx) on a single host.
- `Caddyfile` — reverse proxy: `/api/*` → api, everything else → the SPA.

Production-shaped images build from the `prod` target of each Dockerfile. Building those images or
starting this reference stack does not supply managed database, HA, backup/PITR, secret rotation,
an external security-event dispatcher or SIEM/WORM sink, monitoring, deployment approval, or
rollback controls. No AWS resource, HA/PITR design, or deployment automation is implemented here.
The current application schema head is `0017`; traffic admission must use
`/api/health/ready`, not process liveness, and must see every readiness dimension report `ok`.
Public OAuth client IDs are read by the static web application at runtime from `/api/auth/config`.
Production rejects malformed Google/Azure client IDs, non-canonical `PUBLIC_ORIGIN`, and proxy trust
that is not an explicit reviewed IP/CIDR network (including wildcard/world-wide trust).

## MySQL 8.0 to 8.4 LTS upgrade policy

The development and CI baseline is MySQL 8.4 LTS. Changing the Compose image tag is **not**
authorization to start 8.4 against an existing 8.0 `mysql_data` volume. Treat that volume as
production-like data until it has been backed up and the upgrade has been rehearsed.
The default local Compose file fails closed until `MYSQL_DATA_VOLUME` explicitly names a fresh or
restore-validated 8.4 volume. This reference topology has no MySQL service and does not consume that
variable; its operator must provide a separately governed database instead of assuming the local
volume safety check applies here.

For an existing environment:

1. Quiesce application writes and take both a tested logical export and a storage-level snapshot.
2. Run MySQL's Upgrade Checker against a clone, resolve every reported incompatibility, and record
   the source/target patch versions and rollback owner.
3. Prefer restoring the logical export into a **fresh** 8.4 volume/instance. Apply `alembic upgrade
   head`, run `alembic check`, schema/integrity checks, representative query plans, and application
   smoke/load tests there.
4. Rehearse rollback by restoring the 8.0 snapshot/export into a separate 8.0 instance. Never point
   8.0 at a data directory that 8.4 has upgraded.
5. Cut over only after backup restore time and validation meet the approved RPO/RTO. Keep the old
   instance read-only and isolated until the rollback window closes.

Any populated path that crosses Alembic `0013` must use the explicit
[0013 DDL maintenance-window procedure](../docs/ENTERPRISE_OPERATIONS.md#0013-ddl-maintenance-window).
Its collation changes, session backfill/constraint, `request_logs.id` type conversion, and indexes
can rebuild tables or wait on metadata locks. Measure affected table/data/index sizes on a clone,
verify a restorable backup, drain every writer, clear long transactions/metadata-lock blockers, run
one migrator, and keep traffic out until the exact `0017` head and every readiness dimension pass.

Production should use a managed 8.4 LTS database with verified TLS, `require_secure_transport`,
private networking, separate runtime/migrator principals, PITR, and regularly exercised restores.
The reference Compose stack does not implement those production controls.

The 2026-07-20 local rehearsal restored a checksum-verified logical export into a fresh
`kingfisher_mysql84_data` volume, advanced Alembic from `0012` to `0013`, passed `alembic check`,
matched source row counts, and passed the backend integration suite. The prior upgraded volume and
logical backup were retained locally. Subsequent retained backups before `0014` and `0017` were
each checksum-verified and restored into disposable MySQL 8.4 instances before the local data
advanced through current head `0017`; row counts, migration drift, and readiness remained clean.
This is useful local restore evidence, but it is not a timed, independently operated production
recovery drill and establishes no production RPO/RTO. See the evidence boundary in
`../docs/ENTERPRISE_OPERATIONS.md` before moving or deleting any retained artifact. Every target
environment must still pass its own current migration and readiness gates.
