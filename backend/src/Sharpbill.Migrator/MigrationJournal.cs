using Dapper;
using MySqlConnector;

namespace Sharpbill.Migrator;

internal sealed record JournalInspection(
    bool TableExists,
    bool BaselineExists,
    IReadOnlyList<ValidationIssue> Issues);

internal static class MigrationJournal
{
    public const string TableName = "sharpbill_schema_history";

    private const string BaselineId = "alembic-0021-baseline";
    private const string SourceRevision = "0021";
    private const string MigratorVersion = "1.0.0";

    public static async Task<JournalInspection> InspectAsync(
        MySqlConnection connection,
        DatabaseSchema schema,
        string snapshotSha256,
        CancellationToken cancellationToken)
    {
        TableMetadata? table = schema.Tables.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, TableName, StringComparison.Ordinal));
        if (table is null)
        {
            return new JournalInspection(false, false, []);
        }

        var issues = new List<ValidationIssue>();
        if (!string.Equals(table.Engine, "InnoDB", StringComparison.Ordinal)
            || !string.Equals(table.Collation, "utf8mb4_0900_ai_ci", StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(
                "C# migration journal",
                "invalid table",
                $"expected InnoDB/utf8mb4_0900_ai_ci; found {table.Engine}/{table.Collation}"));
        }

        string[] expectedColumns =
        [
            "001|migration_id|varchar(100)|NO|utf8mb4_0900_bin",
            "002|source_revision|varchar(32)|NO|utf8mb4_0900_bin",
            "003|snapshot_sha256|char(64)|NO|ascii_bin",
            "004|migrator_version|varchar(32)|NO|utf8mb4_0900_bin",
            "005|applied_at|datetime(6)|NO|<NULL>",
        ];
        string[] actualColumns = schema.Columns
            .Where(column => string.Equals(column.TableName, TableName, StringComparison.Ordinal))
            .Select(column => string.Join(
                '|',
                column.Ordinal.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                column.Name,
                column.ColumnType.ToLowerInvariant(),
                column.IsNullable ? "YES" : "NO",
                column.Collation ?? "<NULL>"))
            .ToArray();
        issues.AddRange(SchemaComparison.CompareExact(
            "C# migration journal columns",
            expectedColumns,
            actualColumns));

        string[] actualIndexes = schema.Indexes
            .Where(index => string.Equals(index.TableName, TableName, StringComparison.Ordinal))
            .Select(index =>
                $"{index.Name}|{(index.IsUnique ? "UNIQUE" : "NONUNIQUE")}|{string.Join(',', index.Columns)}")
            .ToArray();
        issues.AddRange(SchemaComparison.CompareExact(
            "C# migration journal indexes",
            ["PRIMARY|UNIQUE|migration_id"],
            actualIndexes));

        if (issues.Count > 0)
        {
            return new JournalInspection(true, false, issues);
        }

        BaselineRow? baseline = await connection.QuerySingleOrDefaultAsync<BaselineRow>(
            new CommandDefinition(
                "SELECT migration_id AS MigrationId, source_revision AS SourceRevision, "
                + "snapshot_sha256 AS SnapshotSha256, migrator_version AS MigratorVersion "
                + "FROM sharpbill_schema_history WHERE migration_id = @BaselineId",
                new { BaselineId },
                cancellationToken: cancellationToken));
        if (baseline is null)
        {
            return new JournalInspection(true, false, []);
        }

        if (!string.Equals(baseline.SourceRevision, SourceRevision, StringComparison.Ordinal)
            || !string.Equals(baseline.SnapshotSha256, snapshotSha256, StringComparison.Ordinal)
            || !string.Equals(baseline.MigratorVersion, MigratorVersion, StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(
                "C# migration journal baseline",
                "modified",
                "the Alembic-0021 baseline does not match this reviewed snapshot/migrator"));
        }

        return new JournalInspection(true, true, issues);
    }

    public static async Task WriteBaselineAsync(
        MySqlConnection connection,
        bool tableExists,
        string snapshotSha256,
        CancellationToken cancellationToken)
    {
        if (!tableExists)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                CreateTableSql,
                cancellationToken: cancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO sharpbill_schema_history "
            + "(migration_id, source_revision, snapshot_sha256, migrator_version) "
            + "VALUES (@BaselineId, @SourceRevision, @SnapshotSha256, @MigratorVersion)",
            new
            {
                BaselineId,
                SourceRevision,
                SnapshotSha256 = snapshotSha256,
                MigratorVersion,
            },
            cancellationToken: cancellationToken));
    }

    private const string CreateTableSql =
        """
        CREATE TABLE `sharpbill_schema_history` (
          `migration_id` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
          `source_revision` varchar(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
          `snapshot_sha256` char(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
          `migrator_version` varchar(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin NOT NULL,
          `applied_at` datetime(6) NOT NULL DEFAULT (now(6)),
          PRIMARY KEY (`migration_id`),
          CONSTRAINT `ck_sharpbill_schema_history_snapshot_sha256_valid`
            CHECK (`snapshot_sha256` REGEXP '^[0-9a-f]{64}$')
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
        """;

    private sealed class BaselineRow
    {
        public required string MigrationId { get; init; }

        public required string SourceRevision { get; init; }

        public required string SnapshotSha256 { get; init; }

        public required string MigratorVersion { get; init; }
    }
}
