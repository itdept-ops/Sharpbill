using Dapper;
using MySqlConnector;

namespace Sharpbill.Migrator;

internal static class SchemaIntrospector
{
    public static async Task<IReadOnlyList<string>> ReadBaseTablesAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
            ORDER BY table_name
            """;
        IEnumerable<string> tables = await connection.QueryAsync<string>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return tables.AsList();
    }

    public static async Task<IReadOnlyList<string>> ReadAlembicRevisionsAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        IEnumerable<string> revisions = await connection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT version_num FROM alembic_version ORDER BY version_num",
                cancellationToken: cancellationToken));
        return revisions.AsList();
    }

    public static async Task<DatabaseSchema> ReadSchemaAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        IEnumerable<TableRow> tableRows = await connection.QueryAsync<TableRow>(
            new CommandDefinition(TableSql, cancellationToken: cancellationToken));
        IEnumerable<ColumnRow> columnRows = await connection.QueryAsync<ColumnRow>(
            new CommandDefinition(ColumnSql, cancellationToken: cancellationToken));
        IEnumerable<IndexRow> indexRows = await connection.QueryAsync<IndexRow>(
            new CommandDefinition(IndexSql, cancellationToken: cancellationToken));
        IEnumerable<ForeignKeyRow> foreignKeyRows = await connection.QueryAsync<ForeignKeyRow>(
            new CommandDefinition(ForeignKeySql, cancellationToken: cancellationToken));
        IEnumerable<CheckRow> checkRows = await connection.QueryAsync<CheckRow>(
            new CommandDefinition(CheckSql, cancellationToken: cancellationToken));

        var tables = tableRows
            .Select(row => new TableMetadata(row.Name, row.Engine, row.Collation))
            .ToArray();
        var columns = columnRows
            .Select(row => new ColumnMetadata(
                row.TableName,
                row.Ordinal,
                row.Name,
                row.ColumnType,
                string.Equals(row.Nullable, "YES", StringComparison.OrdinalIgnoreCase),
                row.DefaultValue,
                row.Extra,
                row.Collation))
            .ToArray();
        var indexes = indexRows
            .GroupBy(
                row => new { row.TableName, row.Name, row.NonUnique, row.Type })
            .Select(group => new IndexMetadata(
                group.Key.TableName,
                group.Key.Name,
                group.Key.NonUnique == 0,
                group.Key.Type,
                group.OrderBy(row => row.Ordinal).Select(row => row.ColumnName).ToArray()))
            .ToArray();
        var foreignKeys = foreignKeyRows
            .GroupBy(
                row => new
                {
                    row.TableName,
                    row.Name,
                    row.ReferencedTable,
                    row.DeleteRule,
                    row.UpdateRule,
                })
            .Select(group => new ForeignKeyMetadata(
                group.Key.TableName,
                group.Key.Name,
                group.OrderBy(row => row.Ordinal).Select(row => row.ColumnName).ToArray(),
                group.Key.ReferencedTable,
                group.OrderBy(row => row.Ordinal).Select(row => row.ReferencedColumn).ToArray(),
                group.Key.DeleteRule,
                group.Key.UpdateRule))
            .ToArray();
        var checks = checkRows
            .Select(row => new CheckMetadata(row.TableName, row.Name, row.Clause))
            .ToArray();

        return new DatabaseSchema(tables, columns, indexes, foreignKeys, checks);
    }

    public static async Task<SeedSnapshot> ReadSeedsAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        IEnumerable<PermissionRow> permissionRows = await connection.QueryAsync<PermissionRow>(
            new CommandDefinition(
                "SELECT id AS Id, `key` AS `Key`, description AS Description, "
                + "is_system AS IsSystem FROM permissions WHERE is_system = 1 ORDER BY `key`",
                cancellationToken: cancellationToken));
        IEnumerable<RoleRow> roleRows = await connection.QueryAsync<RoleRow>(
            new CommandDefinition(
                "SELECT id AS Id, name AS Name, description AS Description, "
                + "is_system AS IsSystem, version AS Version "
                + "FROM roles WHERE is_system = 1 ORDER BY name",
                cancellationToken: cancellationToken));
        IEnumerable<RolePermissionRow> grantRows =
            await connection.QueryAsync<RolePermissionRow>(
                new CommandDefinition(
                    "SELECT rp.role_id AS RoleId, rp.permission_id AS PermissionId "
                    + "FROM role_permissions rp "
                    + "JOIN roles r ON r.id = rp.role_id "
                    + "JOIN permissions p ON p.id = rp.permission_id "
                    + "WHERE r.is_system = 1 AND p.is_system = 1 "
                    + "ORDER BY rp.role_id, rp.permission_id",
                    cancellationToken: cancellationToken));
        IEnumerable<SiteSettingsRow> settingsRows =
            await connection.QueryAsync<SiteSettingsRow>(
                new CommandDefinition(
                    "SELECT id AS Id, signup_mode AS SignupMode, allow_google AS AllowGoogle, "
                    + "allow_microsoft AS AllowMicrosoft, default_role_id AS DefaultRoleId, "
                    + "calm_mode AS CalmMode, retention_hold AS RetentionHold, "
                    + "retention_hold_reference AS RetentionHoldReference "
                    + "FROM site_settings ORDER BY id",
                    cancellationToken: cancellationToken));

        return new SeedSnapshot(
            permissionRows
                .Select(row => new PermissionSeed(
                    row.Id,
                    row.Key,
                    row.Description,
                    row.IsSystem != 0))
                .ToArray(),
            roleRows
                .Select(row => new RoleSeed(
                    row.Id,
                    row.Name,
                    row.Description,
                    row.IsSystem != 0,
                    row.Version))
                .ToArray(),
            grantRows
                .Select(row => new RolePermissionSeed(row.RoleId, row.PermissionId))
                .ToArray(),
            settingsRows
                .Select(row => new SiteSettingsSeed(
                    row.Id,
                    row.SignupMode,
                    row.AllowGoogle != 0,
                    row.AllowMicrosoft != 0,
                    row.DefaultRoleId,
                    row.CalmMode != 0,
                    row.RetentionHold != 0,
                    row.RetentionHoldReference))
                .ToArray());
    }

    private const string TableSql =
        """
        SELECT table_name AS Name, engine AS Engine, table_collation AS Collation
        FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
        ORDER BY table_name
        """;

    private const string ColumnSql =
        """
        SELECT table_name AS TableName, ordinal_position AS Ordinal, column_name AS Name,
               column_type AS ColumnType, is_nullable AS Nullable,
               CAST(column_default AS CHAR) AS DefaultValue, extra AS Extra,
               collation_name AS Collation
        FROM information_schema.columns
        WHERE table_schema = DATABASE()
        ORDER BY table_name, ordinal_position
        """;

    private const string IndexSql =
        """
        SELECT table_name AS TableName, index_name AS Name, non_unique AS NonUnique,
               seq_in_index AS Ordinal, column_name AS ColumnName, index_type AS Type
        FROM information_schema.statistics
        WHERE table_schema = DATABASE()
        ORDER BY table_name, index_name, seq_in_index
        """;

    private const string ForeignKeySql =
        """
        SELECT k.table_name AS TableName, k.constraint_name AS Name,
               k.ordinal_position AS Ordinal, k.column_name AS ColumnName,
               k.referenced_table_name AS ReferencedTable,
               k.referenced_column_name AS ReferencedColumn,
               r.delete_rule AS DeleteRule, r.update_rule AS UpdateRule
        FROM information_schema.key_column_usage k
        JOIN information_schema.referential_constraints r
          ON r.constraint_schema = k.constraint_schema
         AND r.constraint_name = k.constraint_name
         AND r.table_name = k.table_name
        WHERE k.table_schema = DATABASE() AND k.referenced_table_name IS NOT NULL
        ORDER BY k.table_name, k.constraint_name, k.ordinal_position
        """;

    private const string CheckSql =
        """
        SELECT tc.table_name AS TableName, cc.constraint_name AS Name,
               cc.check_clause AS Clause
        FROM information_schema.check_constraints cc
        JOIN information_schema.table_constraints tc
          ON tc.constraint_schema = cc.constraint_schema
         AND tc.constraint_name = cc.constraint_name
        WHERE cc.constraint_schema = DATABASE() AND tc.constraint_type = 'CHECK'
        ORDER BY tc.table_name, cc.constraint_name
        """;

    private sealed class TableRow
    {
        public required string Name { get; init; }

        public required string Engine { get; init; }

        public required string Collation { get; init; }
    }

    private sealed class ColumnRow
    {
        public required string TableName { get; init; }

        public int Ordinal { get; init; }

        public required string Name { get; init; }

        public required string ColumnType { get; init; }

        public required string Nullable { get; init; }

        public string? DefaultValue { get; init; }

        public required string Extra { get; init; }

        public string? Collation { get; init; }
    }

    private sealed class IndexRow
    {
        public required string TableName { get; init; }

        public required string Name { get; init; }

        public int NonUnique { get; init; }

        public int Ordinal { get; init; }

        public required string ColumnName { get; init; }

        public required string Type { get; init; }
    }

    private sealed class ForeignKeyRow
    {
        public required string TableName { get; init; }

        public required string Name { get; init; }

        public int Ordinal { get; init; }

        public required string ColumnName { get; init; }

        public required string ReferencedTable { get; init; }

        public required string ReferencedColumn { get; init; }

        public required string DeleteRule { get; init; }

        public required string UpdateRule { get; init; }
    }

    private sealed class CheckRow
    {
        public required string TableName { get; init; }

        public required string Name { get; init; }

        public required string Clause { get; init; }
    }

    private sealed class PermissionRow
    {
        public long Id { get; init; }

        public required string Key { get; init; }

        public string? Description { get; init; }

        public int IsSystem { get; init; }
    }

    private sealed class RoleRow
    {
        public long Id { get; init; }

        public required string Name { get; init; }

        public string? Description { get; init; }

        public int IsSystem { get; init; }

        public int Version { get; init; }
    }

    private sealed class RolePermissionRow
    {
        public long RoleId { get; init; }

        public long PermissionId { get; init; }
    }

    private sealed class SiteSettingsRow
    {
        public long Id { get; init; }

        public required string SignupMode { get; init; }

        public int AllowGoogle { get; init; }

        public int AllowMicrosoft { get; init; }

        public long DefaultRoleId { get; init; }

        public int CalmMode { get; init; }

        public int RetentionHold { get; init; }

        public string? RetentionHoldReference { get; init; }
    }

}
