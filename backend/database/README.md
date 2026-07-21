# Database compatibility bridge

`schema-0021.sql` is the reviewed terminal snapshot of the historical Python/Alembic schema.
It is not an editable migration chain and it never runs against a non-empty database.

The C# migrator has two deliberately narrow paths:

- An empty database receives this snapshot, its canonical RBAC/site-settings seeds, the
  `alembic_version=0021` marker, and a C# baseline journal entry.
- An existing database must already report exactly Alembic revision `0021`. The migrator
  performs read-only checks for table shape, critical columns and collations, indexes,
  foreign keys, check constraints, and immutable seeds before it creates the C# journal.

Older, newer, unknown, and partially initialized databases are refused. MySQL DDL auto-commits,
so a failed empty-database initialization is intentionally left visible and will be refused as
partial on the next run; operators must investigate instead of allowing automatic repair.

A database below `0021` is outside the active migrator's supported path. Recovery requires an
operator-owned, explicitly approved, source-matched archival migration artifact with verified
provenance and digest; this repository does not build or publish one. The frozen Alembic files are
evidence, not a runnable recovery artifact.

The Alembic files under `../alembic/` are historical evidence and must remain unchanged.
