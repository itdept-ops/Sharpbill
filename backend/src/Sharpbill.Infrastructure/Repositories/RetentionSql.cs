using Dapper;
using MySqlConnector;

namespace Sharpbill.Infrastructure.Repositories;

internal static class RetentionSql
{
    public static async Task<bool> IsHoldActiveAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT retention_hold
            FROM site_settings
            WHERE id = 1
            FOR UPDATE
            """;
        bool? hold = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            sql,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return hold ?? throw new InvalidOperationException(
            "Site settings are missing; retention cannot make a safe hold decision.");
    }
}
