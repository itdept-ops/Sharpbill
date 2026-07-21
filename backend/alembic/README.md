# Historical Alembic schema provenance

This directory preserves Sharpbill's immutable Alembic revisions `0001` through `0021` and their
historical scaffolding. They document how the compatibility schema evolved and support review of
the terminal `database/schema-0021.sql` snapshot. They are not part of the active backend,
container, CI, or deployment runtime.

Current schema authority belongs exclusively to the C# `Sharpbill.Migrator`. It applies the
reviewed `0021` snapshot to an empty database, or validates and journals an existing database that
already matches exact revision `0021`. The ASP.NET Core API never mutates schema at startup.

Do not edit, reorder, regenerate, or execute the files in this directory as an active migration
chain. The current repository intentionally provides no Python migration environment or supported
entry point; these files alone are not a runnable recovery artifact.

A database below `0021` is outside the active migrator's supported path. Recovering one requires an
operator-owned, explicitly approved, source-matched archival migration artifact whose provenance
and digest have been verified. This repository neither builds nor publishes that artifact. Use an
approved artifact only against an isolated clone under the documented maintenance-window and
backup/restore controls, then validate the exact `0021` result with `Sharpbill.Migrator`. If no such
artifact exists, stop and establish a separately approved data-recovery plan.
