using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class NonceRepository(DatabaseSession session) : DapperRepository(session), INonceRepository
{
    public async Task<int> CountActiveAsync(DateTime now, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM login_nonces WHERE expires_at > @Now";
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(Command(
            sql,
            new { Now = RepositoryMapping.ToDatabaseUtc(now) },
            cancellationToken)).ConfigureAwait(false);
    }

    public async Task AddAsync(LoginNonce nonce, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO login_nonces (nonce, created_at, expires_at)
            VALUES (@Nonce, @CreatedAt, @ExpiresAt)
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(sql, new
        {
            nonce.Nonce,
            CreatedAt = RepositoryMapping.ToDatabaseUtc(nonce.CreatedAt),
            ExpiresAt = RepositoryMapping.ToDatabaseUtc(nonce.ExpiresAt),
        }, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ConsumeAsync(
        string nonce,
        DateTime now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM login_nonces
            WHERE nonce = @Nonce AND expires_at > @Now
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(Command(sql, new
        {
            Nonce = nonce,
            Now = RepositoryMapping.ToDatabaseUtc(now),
        }, cancellationToken)).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<int> PruneExpiredAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(limit, 1, 5_000);
        return await Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            const string selectSql = """
                SELECT nonce
                FROM login_nonces
                WHERE expires_at <= @Now
                ORDER BY expires_at, nonce
                LIMIT @Limit
                FOR UPDATE SKIP LOCKED
                """;
            string[] expired = (await connection.QueryAsync<string>(TransactionalCommand(
                selectSql,
                new { Now = RepositoryMapping.ToDatabaseUtc(now), Limit = boundedLimit },
                transaction,
                token)).ConfigureAwait(false)).AsList().ToArray();
            if (expired.Length == 0)
            {
                return 0;
            }

            const string deleteSql = "DELETE FROM login_nonces WHERE nonce IN @Nonces";
            return await connection.ExecuteAsync(TransactionalCommand(
                deleteSql,
                new { Nonces = expired },
                transaction,
                token)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
