using Dapper;
using MySqlConnector;

namespace Sharpbill.Migrator;

internal sealed class DatabaseMigrator
{
    private readonly ConsoleReporter _reporter;

    public DatabaseMigrator(ConsoleReporter reporter)
    {
        _reporter = reporter;
    }

    public async Task<ExitCode> RunAsync(
        CliOptions options,
        string connectionString,
        CancellationToken cancellationToken)
    {
        if (options.Command == MigratorCommand.SeedDemo && !DemoSeeder.IsAllowed())
        {
            _reporter.Error(
                "SBM0024",
                "Refusing demo data outside an explicitly local environment. Set APP_ENV=local "
                + "or SHARPBILL_ALLOW_DEMO_SEED=true only for an isolated demo database.");
            return ExitCode.DemoSeedRefused;
        }

        SchemaSnapshotResource snapshot = await SchemaSnapshotResource.LoadAsync(cancellationToken);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        string? databaseName = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT DATABASE()",
            cancellationToken: cancellationToken));
        if (string.IsNullOrEmpty(databaseName))
        {
            throw new InvalidOperationException("The connection did not select a database.");
        }
        _reporter.Info("SBM0001", $"Selected database '{databaseName}'.");

        if (options.Command == MigratorCommand.Validate)
        {
            InspectionOutcome inspection = await InspectAsync(
                connection,
                snapshot,
                cancellationToken);
            return ReportValidation(inspection);
        }

        await using DatabaseMigrationLock? migrationLock =
            await DatabaseMigrationLock.TryAcquireAsync(
                connection,
                options.LockTimeoutSeconds,
                cancellationToken);
        if (migrationLock is null)
        {
            _reporter.Error(
                "SBM0031",
                $"Could not acquire the database migration lock within "
                + $"{options.LockTimeoutSeconds} seconds.");
            return ExitCode.LockUnavailable;
        }

        ExitCode migrationResult = await EnsureMigratedAsync(
            connection,
            snapshot,
            options.DryRun,
            cancellationToken);
        if (migrationResult != ExitCode.Success
            || options.Command != MigratorCommand.SeedDemo)
        {
            return migrationResult;
        }

        if (options.DryRun)
        {
            _reporter.Info(
                "SBM0101",
                "Dry run: would idempotently seed Manager, Auditor, and 12 demo users.");
            return ExitCode.Success;
        }

        DemoSeedResult seedResult = await DemoSeeder.SeedAsync(connection, cancellationToken);
        _reporter.Info(
            "SBM0100",
            $"Demo seed complete: {seedResult.NewRoles} new roles, {seedResult.NewUsers} new "
            + $"users, {seedResult.TotalUsers} total users.");
        return ExitCode.Success;
    }

    private async Task<ExitCode> EnsureMigratedAsync(
        MySqlConnection connection,
        SchemaSnapshotResource snapshot,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        InspectionOutcome inspection = await InspectAsync(connection, snapshot, cancellationToken);
        if (inspection.IsEmpty)
        {
            if (dryRun)
            {
                _reporter.Info(
                    "SBM0004",
                    $"Dry run: database is empty; would execute {snapshot.Statements.Count} "
                    + $"reviewed 0021 statements (SHA-256 {snapshot.Sha256}) and journal the baseline.");
                return ExitCode.Success;
            }

            _reporter.Info(
                "SBM0005",
                $"Database is empty; applying reviewed Alembic-0021 snapshot "
                + $"SHA-256 {snapshot.Sha256}.");
            foreach (string statement in snapshot.Statements)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    statement,
                    cancellationToken: cancellationToken));
            }

            inspection = await InspectAsync(connection, snapshot, cancellationToken);
            if (!inspection.IsCompatible)
            {
                _reporter.Error(
                    "SBM0030",
                    "The empty-database snapshot did not pass post-apply validation. MySQL DDL "
                    + "auto-commits; the visible partial schema was retained for investigation.");
                return inspection.ExitCode;
            }
        }
        else if (!inspection.IsCompatible)
        {
            return ReportValidation(inspection);
        }

        JournalInspection journal = inspection.Journal
            ?? throw new InvalidOperationException("Compatible schema inspection omitted journal state.");
        if (journal.BaselineExists)
        {
            _reporter.Info(
                "SBM0000",
                "Schema is compatible with Alembic 0021 and the C# baseline is already journaled.");
            return ExitCode.Success;
        }

        if (dryRun)
        {
            _reporter.Info(
                "SBM0006",
                "Dry run: existing Alembic-0021 schema is exact; would write only the C# "
                + "baseline journal (no application-schema DDL). ");
            return ExitCode.Success;
        }

        await MigrationJournal.WriteBaselineAsync(
            connection,
            journal.TableExists,
            snapshot.Sha256,
            cancellationToken);

        InspectionOutcome verified = await InspectAsync(connection, snapshot, cancellationToken);
        if (!verified.IsCompatible || verified.Journal?.BaselineExists != true)
        {
            _reporter.Error("SBM0030", "The C# migration baseline could not be verified after writing.");
            return ExitCode.MigrationFailed;
        }

        _reporter.Info(
            "SBM0000",
            "Alembic-0021 compatibility validated and C# migration baseline journaled.");
        return ExitCode.Success;
    }

    private static async Task<InspectionOutcome> InspectAsync(
        MySqlConnection connection,
        SchemaSnapshotResource snapshot,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> tables = await SchemaIntrospector.ReadBaseTablesAsync(
            connection,
            cancellationToken);
        if (tables.Count == 0)
        {
            return InspectionOutcome.Empty();
        }

        if (!tables.Contains("alembic_version", StringComparer.Ordinal))
        {
            return InspectionOutcome.Failure(
                ExitCode.PartialDatabase,
                "The database is non-empty but has no alembic_version table. Refusing an "
                + "unknown or partially initialized schema.");
        }

        IReadOnlyList<string> revisions;
        try
        {
            revisions = await SchemaIntrospector.ReadAlembicRevisionsAsync(connection, cancellationToken);
        }
        catch (MySqlException exception)
        {
            return InspectionOutcome.Failure(
                ExitCode.PartialDatabase,
                $"The Alembic version table is malformed: {exception.Message}");
        }

        if (revisions.Count != 1
            || !string.Equals(revisions[0], "0021", StringComparison.Ordinal))
        {
            string found = revisions.Count == 0 ? "<empty>" : string.Join(", ", revisions);
            return InspectionOutcome.Failure(
                ExitCode.UnsupportedAlembicRevision,
                $"Expected the single terminal Alembic revision 0021; found {found}. "
                + "Older, newer, branched, and unknown revisions require operator review.");
        }

        DatabaseSchema schema = await SchemaIntrospector.ReadSchemaAsync(connection, cancellationToken);
        SeedSnapshot seeds;
        try
        {
            seeds = await SchemaIntrospector.ReadSeedsAsync(connection, cancellationToken);
        }
        catch (MySqlException exception)
        {
            return InspectionOutcome.Failure(
                ExitCode.SchemaValidationFailed,
                $"The declared 0021 schema is missing required seed tables or columns: "
                + exception.Message);
        }

        ValidationResult validation = SchemaValidator.Validate(schema, seeds);
        if (!validation.IsValid)
        {
            return InspectionOutcome.Failure(
                ExitCode.SchemaValidationFailed,
                "The database declares Alembic 0021 but its structure or canonical seeds drifted.",
                validation.Issues);
        }

        JournalInspection journal = await MigrationJournal.InspectAsync(
            connection,
            schema,
            snapshot.Sha256,
            cancellationToken);
        if (journal.Issues.Count > 0)
        {
            return InspectionOutcome.Failure(
                ExitCode.SchemaValidationFailed,
                "The C# schema-history journal is malformed or does not match this snapshot.",
                journal.Issues);
        }

        return InspectionOutcome.Compatible(journal);
    }

    private ExitCode ReportValidation(InspectionOutcome inspection)
    {
        if (inspection.IsEmpty)
        {
            _reporter.Error(
                "SBM0020",
                "The selected database is empty. Run 'migrate' to apply the reviewed 0021 snapshot.");
            return ExitCode.EmptyDatabaseRequiresMigration;
        }

        if (!inspection.IsCompatible)
        {
            string code = inspection.ExitCode switch
            {
                ExitCode.UnsupportedAlembicRevision => "SBM0021",
                ExitCode.SchemaValidationFailed => "SBM0022",
                ExitCode.PartialDatabase => "SBM0023",
                _ => "SBM0030",
            };
            _reporter.Error(code, inspection.Message ?? "Database validation failed.");
            foreach (ValidationIssue issue in inspection.Issues)
            {
                _reporter.Error(code, issue.ToString());
            }

            return inspection.ExitCode;
        }

        string journalStatus = inspection.Journal?.BaselineExists == true
            ? "C# baseline journal present"
            : "C# baseline journal pending; migrate will write it";
        _reporter.Info(
            "SBM0000",
            $"Schema, collations, constraints, indexes, and canonical seeds match Alembic 0021; "
            + journalStatus + ".");
        return ExitCode.Success;
    }

    private sealed record InspectionOutcome(
        bool IsEmpty,
        bool IsCompatible,
        ExitCode ExitCode,
        string? Message,
        IReadOnlyList<ValidationIssue> Issues,
        JournalInspection? Journal)
    {
        public static InspectionOutcome Empty()
        {
            return new InspectionOutcome(
                true,
                false,
                ExitCode.EmptyDatabaseRequiresMigration,
                null,
                [],
                null);
        }

        public static InspectionOutcome Compatible(JournalInspection journal)
        {
            return new InspectionOutcome(false, true, ExitCode.Success, null, [], journal);
        }

        public static InspectionOutcome Failure(
            ExitCode exitCode,
            string message,
            IReadOnlyList<ValidationIssue>? issues = null)
        {
            return new InspectionOutcome(
                false,
                false,
                exitCode,
                message,
                issues ?? [],
                null);
        }
    }
}
