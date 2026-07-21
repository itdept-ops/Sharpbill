using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class PermissionRepository(DatabaseSession session)
    : DapperRepository(session), IPermissionRepository
{
    private const string Columns = "id, `key`, description, is_system, created_at, updated_at";

    public async Task<Permission?> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        string sql = $"SELECT {Columns} FROM permissions WHERE `key` = @Key LIMIT 1";
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        PermissionRow? row = await connection.QuerySingleOrDefaultAsync<PermissionRow>(Command(
            sql,
            new { Key = key },
            cancellationToken)).ConfigureAwait(false);
        return row is null ? null : RepositoryMapping.ToEntity(row);
    }

    public async Task<IReadOnlyList<Permission>> FindByKeysAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        string[] normalized = keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (normalized.Length == 0)
        {
            return [];
        }

        string sql = $"SELECT {Columns} FROM permissions WHERE `key` IN @Keys ORDER BY `key`";
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<PermissionRow> rows = await connection.QueryAsync<PermissionRow>(Command(
            sql,
            new { Keys = normalized },
            cancellationToken)).ConfigureAwait(false);
        return rows.Select(RepositoryMapping.ToEntity).ToArray();
    }

    public async Task<IReadOnlyList<Permission>> ListAsync(CancellationToken cancellationToken)
    {
        string sql = $"SELECT {Columns} FROM permissions ORDER BY `key`";
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<PermissionRow> rows = await connection.QueryAsync<PermissionRow>(Command(
            sql,
            null,
            cancellationToken)).ConfigureAwait(false);
        return rows.Select(RepositoryMapping.ToEntity).ToArray();
    }

    public async Task<int> AddAsync(Permission permission, CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO permissions (`key`, description, is_system, created_at, updated_at)
            VALUES (@Key, @Description, @IsSystem, @CreatedAt, @UpdatedAt)
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(insertSql, new
        {
            permission.Key,
            permission.Description,
            permission.IsSystem,
            CreatedAt = RepositoryMapping.ToDatabaseUtc(permission.CreatedAt),
            UpdatedAt = RepositoryMapping.ToDatabaseUtc(permission.UpdatedAt),
        }, cancellationToken)).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(Command(
            "SELECT LAST_INSERT_ID()",
            null,
            cancellationToken)).ConfigureAwait(false);
    }
}
