using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class SessionRepository(DatabaseSession session) : DapperRepository(session), ISessionRepository
{
    private const string Columns = """
        id, user_id, jti, user_agent, ip AS ip_address, created_at,
        last_seen_at, revoked_at, expires_at
        """;

    public Task<UserSession?> FindByJtiAsync(
        Guid jti,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        FindCoreAsync(
            "jti = @Value",
            jti.ToString("D"),
            forUpdate ? "FOR UPDATE" : string.Empty,
            cancellationToken);

    public Task<UserSession?> FindByJtiForAuthenticationAsync(
        Guid jti,
        CancellationToken cancellationToken) =>
        FindCoreAsync("jti = @Value", jti.ToString("D"), "FOR SHARE", cancellationToken);

    public Task<UserSession?> FindAsync(
        int sessionId,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        FindCoreAsync(
            "id = @Value",
            sessionId,
            forUpdate ? "FOR UPDATE" : string.Empty,
            cancellationToken);

    public async Task<IReadOnlyList<UserSession>> ListActiveAsync(
        int userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT {Columns}
            FROM user_sessions
            WHERE user_id = @UserId
              AND revoked_at IS NULL
              AND expires_at > @Now
            ORDER BY created_at, id
            FOR UPDATE
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<SessionRow> rows = await connection.QueryAsync<SessionRow>(Command(sql, new
        {
            UserId = userId,
            Now = RepositoryMapping.ToDatabaseUtc(now),
        }, cancellationToken)).ConfigureAwait(false);
        return rows.Select(ToEntity).ToArray();
    }

    public async Task<int> CountActiveAsync(
        int userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM user_sessions
            WHERE user_id = @UserId
              AND revoked_at IS NULL
              AND expires_at > @Now
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(Command(sql, new
        {
            UserId = userId,
            Now = RepositoryMapping.ToDatabaseUtc(now),
        }, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> AddAsync(UserSession session, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO user_sessions
                (user_id, jti, user_agent, ip, created_at, last_seen_at, revoked_at, expires_at)
            VALUES
                (@UserId, @Jti, @UserAgent, @IpAddress, @CreatedAt, @LastSeenAt, @RevokedAt, @ExpiresAt);
            SELECT LAST_INSERT_ID();
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<int>(Command(sql, new
        {
            session.UserId,
            Jti = session.Jti.ToString("D"),
            session.UserAgent,
            session.IpAddress,
            CreatedAt = RepositoryMapping.ToDatabaseUtc(session.CreatedAt),
            LastSeenAt = session.LastSeenAt is null
                ? (DateTime?)null
                : RepositoryMapping.ToDatabaseUtc(session.LastSeenAt.Value),
            RevokedAt = session.RevokedAt is null
                ? (DateTime?)null
                : RepositoryMapping.ToDatabaseUtc(session.RevokedAt.Value),
            ExpiresAt = RepositoryMapping.ToDatabaseUtc(session.ExpiresAt),
        }, cancellationToken)).ConfigureAwait(false);
    }

    public async Task TouchAsync(
        int sessionId,
        DateTime seenAt,
        DateTime staleBefore,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE user_sessions
            SET last_seen_at = @SeenAt
            WHERE id = @SessionId
              AND revoked_at IS NULL
              AND expires_at > @SeenAt
              AND (last_seen_at IS NULL OR last_seen_at < @StaleBefore)
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(sql, new
        {
            SessionId = sessionId,
            SeenAt = RepositoryMapping.ToDatabaseUtc(seenAt),
            StaleBefore = RepositoryMapping.ToDatabaseUtc(staleBefore),
        }, cancellationToken)).ConfigureAwait(false);
    }

    public async Task RevokeAsync(int sessionId, DateTime revokedAt, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE user_sessions
            SET revoked_at = @RevokedAt
            WHERE id = @SessionId AND revoked_at IS NULL
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(sql, new
        {
            SessionId = sessionId,
            RevokedAt = RepositoryMapping.ToDatabaseUtc(revokedAt),
        }, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> RevokeAllAsync(
        int userId,
        DateTime revokedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE user_sessions
            SET revoked_at = @RevokedAt
            WHERE user_id = @UserId AND revoked_at IS NULL
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Command(sql, new
        {
            UserId = userId,
            RevokedAt = RepositoryMapping.ToDatabaseUtc(revokedAt),
        }, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(DateTime cutoff, int limit, CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(limit, 1, 5_000);
        return await Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            if (await RetentionSql.IsHoldActiveAsync(connection, transaction, token).ConfigureAwait(false))
            {
                return 0;
            }

            const string selectSql = """
                SELECT id
                FROM user_sessions
                WHERE expires_at <= @Cutoff OR revoked_at <= @Cutoff
                ORDER BY LEAST(expires_at, COALESCE(revoked_at, expires_at)), id
                LIMIT @Limit
                FOR UPDATE SKIP LOCKED
                """;
            long[] ids = (await connection.QueryAsync<long>(TransactionalCommand(
                selectSql,
                new { Cutoff = RepositoryMapping.ToDatabaseUtc(cutoff), Limit = boundedLimit },
                transaction,
                token)).ConfigureAwait(false)).AsList().ToArray();
            if (ids.Length == 0)
            {
                return 0;
            }

            return await connection.ExecuteAsync(TransactionalCommand(
                "DELETE FROM user_sessions WHERE id IN @Ids",
                new { Ids = ids },
                transaction,
                token)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UserSession?> FindCoreAsync(
        string predicate,
        object value,
        string lockClause,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT {Columns}
            FROM user_sessions
            WHERE {predicate}
            LIMIT 1
            {lockClause}
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SessionRow? row = await connection.QuerySingleOrDefaultAsync<SessionRow>(Command(
            sql,
            new { Value = value },
            cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToEntity(row);
    }

    private static UserSession ToEntity(SessionRow row) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        Jti = Guid.Parse(row.Jti),
        UserAgent = row.UserAgent,
        IpAddress = row.IpAddress,
        CreatedAt = RepositoryMapping.FromDatabaseUtc(row.CreatedAt),
        LastSeenAt = RepositoryMapping.FromDatabaseUtc(row.LastSeenAt),
        RevokedAt = RepositoryMapping.FromDatabaseUtc(row.RevokedAt),
        ExpiresAt = RepositoryMapping.FromDatabaseUtc(row.ExpiresAt),
    };

    private sealed class SessionRow
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Jti { get; set; } = string.Empty;
        public string? UserAgent { get; set; }
        public string? IpAddress { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public DateTime? RevokedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
