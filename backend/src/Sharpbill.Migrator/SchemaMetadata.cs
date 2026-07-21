namespace Sharpbill.Migrator;

internal sealed record TableMetadata(string Name, string Engine, string Collation);

internal sealed record ColumnMetadata(
    string TableName,
    int Ordinal,
    string Name,
    string ColumnType,
    bool IsNullable,
    string? DefaultValue,
    string Extra,
    string? Collation);

internal sealed record IndexMetadata(
    string TableName,
    string Name,
    bool IsUnique,
    string Type,
    IReadOnlyList<string> Columns);

internal sealed record ForeignKeyMetadata(
    string TableName,
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    string DeleteRule,
    string UpdateRule);

internal sealed record CheckMetadata(string TableName, string Name, string Clause);

internal sealed record DatabaseSchema(
    IReadOnlyList<TableMetadata> Tables,
    IReadOnlyList<ColumnMetadata> Columns,
    IReadOnlyList<IndexMetadata> Indexes,
    IReadOnlyList<ForeignKeyMetadata> ForeignKeys,
    IReadOnlyList<CheckMetadata> Checks);

internal sealed record PermissionSeed(
    long Id,
    string Key,
    string? Description,
    bool IsSystem);

internal sealed record RoleSeed(
    long Id,
    string Name,
    string? Description,
    bool IsSystem,
    int Version);

internal sealed record RolePermissionSeed(long RoleId, long PermissionId);

internal sealed record SiteSettingsSeed(
    long Id,
    string SignupMode,
    bool AllowGoogle,
    bool AllowMicrosoft,
    long DefaultRoleId,
    bool CalmMode,
    bool RetentionHold,
    string? RetentionHoldReference);

internal sealed record SeedSnapshot(
    IReadOnlyList<PermissionSeed> Permissions,
    IReadOnlyList<RoleSeed> Roles,
    IReadOnlyList<RolePermissionSeed> RolePermissions,
    IReadOnlyList<SiteSettingsSeed> SiteSettings);
