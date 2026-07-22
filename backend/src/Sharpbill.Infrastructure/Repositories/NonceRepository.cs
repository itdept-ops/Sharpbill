using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Domain.Constants;
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

    public async Task<bool> TryAddWithinCapacityAsync(
        LoginNonce nonce,
        DateTime now,
        int maximumOutstanding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        if (maximumOutstanding < DomainLimits.LoginNonceAdmissionShards)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumOutstanding),
                $"Nonce capacity must be at least {DomainLimits.LoginNonceAdmissionShards}.");
        }

        if (Session.Transaction is not null)
        {
            throw new InvalidOperationException(
                "Nonce admission owns its short transaction and cannot run inside an ambient unit of work.");
        }

        int shard = GetShardIndex(nonce.Nonce);
        int shardCapacity = GetShardCapacity(maximumOutstanding, shard);
        MySqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string? databaseName = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT DATABASE()",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("Nonce admission requires a selected database.");
        }

        // The database fingerprint prevents unrelated schemas on one server from sharing locks.
        string databaseFingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(databaseName)))[..16];
        string lockName = $"sharpbill:nonce:{databaseFingerprint}:{shard:D2}";
        int? acquired = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT GET_LOCK(@LockName, 0)",
            new { LockName = lockName },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (acquired != 1)
        {
            return false;
        }

        Exception? pendingException = null;
        try
        {
            await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken).ConfigureAwait(false);
            try
            {
                const string countSql = """
                    SELECT COUNT(*)
                    FROM login_nonces
                    WHERE LEFT(nonce, 1) = @Prefix AND expires_at > @Now
                    """;
                int activeInShard = await connection.ExecuteScalarAsync<int>(TransactionalCommand(
                    countSql,
                    new
                    {
                        Prefix = nonce.Nonce[..1],
                        Now = RepositoryMapping.ToDatabaseUtc(now),
                    },
                    transaction,
                    cancellationToken)).ConfigureAwait(false);
                if (activeInShard >= shardCapacity)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }

                const string insertSql = """
                    INSERT INTO login_nonces (nonce, created_at, expires_at)
                    VALUES (@Nonce, @CreatedAt, @ExpiresAt)
                    """;
                _ = await connection.ExecuteAsync(TransactionalCommand(
                    insertSql,
                    new
                    {
                        nonce.Nonce,
                        CreatedAt = RepositoryMapping.ToDatabaseUtc(nonce.CreatedAt),
                        ExpiresAt = RepositoryMapping.ToDatabaseUtc(nonce.ExpiresAt),
                    },
                    transaction,
                    cancellationToken)).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception exception)
        {
            pendingException = exception;
            throw;
        }
        finally
        {
            try
            {
                _ = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT RELEASE_LOCK(@LockName)",
                    new { LockName = lockName },
                    cancellationToken: CancellationToken.None)).ConfigureAwait(false);
            }
            catch when (pendingException is not null)
            {
                // Preserve the transaction/admission exception; a broken connection releases its locks.
            }
        }
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

    internal static int GetShardIndex(string nonce)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        int shard = alphabet.IndexOf(nonce[0], StringComparison.Ordinal);
        return shard >= 0
            ? shard
            : throw new ArgumentException("Nonce is not base64url encoded.", nameof(nonce));
    }

    internal static int GetShardCapacity(int maximumOutstanding, int shard)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumOutstanding,
            DomainLimits.LoginNonceAdmissionShards);
        ArgumentOutOfRangeException.ThrowIfNegative(shard);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            shard,
            DomainLimits.LoginNonceAdmissionShards);
        int baseline = maximumOutstanding / DomainLimits.LoginNonceAdmissionShards;
        return baseline +
            (shard < maximumOutstanding % DomainLimits.LoginNonceAdmissionShards ? 1 : 0);
    }
}
