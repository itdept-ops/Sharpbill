using System.Data;
using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class RoleRepository(DatabaseSession session) : DapperRepository(session), IRoleRepository
{
    private const string Columns = "id, name, description, is_system, version, created_at, updated_at";

    public Task<Role?> FindAsync(int roleId, bool forUpdate, CancellationToken cancellationToken) =>
        FindCoreAsync("id = @Value", roleId, forUpdate, cancellationToken);

    public Task<Role?> FindByNameAsync(
        string name,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        FindCoreAsync("name = @Value", name, forUpdate, cancellationToken);

    public async Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken)
    {
        string sql = $"SELECT {Columns} FROM roles ORDER BY id";
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        RoleRow[] rows = (await connection.QueryAsync<RoleRow>(Command(
            sql,
            null,
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        return await HydrateAsync(rows, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetUserCountsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.id AS role_id, COUNT(u.id) AS user_count
            FROM roles r
            LEFT JOIN users u ON u.role_id = r.id
            GROUP BY r.id
            ORDER BY r.id
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<RoleCountRow> rows = await connection.QueryAsync<RoleCountRow>(Command(
            sql,
            null,
            cancellationToken)).ConfigureAwait(false);
        return rows.ToDictionary(static row => row.RoleId, static row => row.UserCount);
    }

    public async Task<int> AddAsync(Role role, CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO roles (name, description, is_system, version, created_at, updated_at)
            VALUES (@Name, @Description, @IsSystem, @Version, @CreatedAt, @UpdatedAt)
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(insertSql, new
        {
            role.Name,
            role.Description,
            role.IsSystem,
            role.Version,
            CreatedAt = RepositoryMapping.ToDatabaseUtc(role.CreatedAt),
            UpdatedAt = RepositoryMapping.ToDatabaseUtc(role.UpdatedAt),
        }, cancellationToken)).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(Command(
            "SELECT LAST_INSERT_ID()",
            null,
            cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Role role, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE roles
            SET name = @Name,
                description = @Description,
                is_system = @IsSystem,
                version = @Version,
                updated_at = @UpdatedAt
            WHERE id = @Id AND version = @ExpectedVersion
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(Command(sql, new
        {
            role.Id,
            role.Name,
            role.Description,
            role.IsSystem,
            role.Version,
            ExpectedVersion = role.Version - 1,
            UpdatedAt = RepositoryMapping.ToDatabaseUtc(role.UpdatedAt),
        }, cancellationToken)).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new DBConcurrencyException("The role changed before the update could be applied.");
        }
    }

    public async Task DeleteAsync(int roleId, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(
            "DELETE FROM roles WHERE id = @RoleId",
            new { RoleId = roleId },
            cancellationToken)).ConfigureAwait(false);
    }

    public async Task ReplacePermissionsAsync(
        int roleId,
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken)
    {
        int[] ids = permissionIds.Distinct().Order().ToArray();
        await Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            _ = await connection.ExecuteAsync(TransactionalCommand(
                "DELETE FROM role_permissions WHERE role_id = @RoleId",
                new { RoleId = roleId },
                transaction,
                token)).ConfigureAwait(false);
            if (ids.Length > 0)
            {
                const string insertSql = """
                    INSERT INTO role_permissions (role_id, permission_id)
                    VALUES (@RoleId, @PermissionId)
                    """;
                _ = await connection.ExecuteAsync(TransactionalCommand(
                    insertSql,
                    ids.Select(permissionId => new { RoleId = roleId, PermissionId = permissionId }),
                    transaction,
                    token)).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Role?> FindCoreAsync(
        string predicate,
        object value,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT {Columns}
            FROM roles
            WHERE {predicate}
            LIMIT 1
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        RoleRow? row = await connection.QuerySingleOrDefaultAsync<RoleRow>(Command(
            sql,
            new { Value = value },
            cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        IReadOnlyList<Role> roles = await HydrateAsync([row], forUpdate, cancellationToken)
            .ConfigureAwait(false);
        return roles[0];
    }

    private async Task<IReadOnlyList<Role>> HydrateAsync(
        IReadOnlyList<RoleRow> rows,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        int[] roleIds = rows.Select(static row => row.Id).ToArray();
        string sql = $"""
            SELECT rp.role_id, p.id, p.`key`, p.description, p.is_system, p.created_at, p.updated_at
            FROM role_permissions rp
            INNER JOIN permissions p ON p.id = rp.permission_id
            WHERE rp.role_id IN @RoleIds
            ORDER BY rp.role_id, p.`key`, p.id
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<RolePermissionRow> permissionRows = await connection.QueryAsync<RolePermissionRow>(Command(
            sql,
            new { RoleIds = roleIds },
            cancellationToken)).ConfigureAwait(false);
        IReadOnlyDictionary<int, Permission[]> permissions = permissionRows
            .GroupBy(static row => row.RoleId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static row => RepositoryMapping.ToEntity(row)).ToArray());

        return rows.Select(row => new Role
        {
            Id = row.Id,
            Name = row.Name,
            Description = row.Description,
            IsSystem = row.IsSystem,
            Version = row.Version,
            CreatedAt = RepositoryMapping.FromDatabaseUtc(row.CreatedAt),
            UpdatedAt = RepositoryMapping.FromDatabaseUtc(row.UpdatedAt),
            Permissions = permissions.GetValueOrDefault(row.Id, []),
        }).ToArray();
    }

    private sealed class RoleRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystem { get; set; }
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class RolePermissionRow : PermissionRow
    {
        public int RoleId { get; set; }
    }

    private sealed class RoleCountRow
    {
        public int RoleId { get; set; }
        public int UserCount { get; set; }
    }
}
