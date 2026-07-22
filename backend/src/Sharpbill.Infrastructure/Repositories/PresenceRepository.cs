using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class PresenceRepository(DatabaseSession session)
    : DapperRepository(session), IPresenceRepository
{
    public async Task<PresenceResponse> GetOnlineAsync(
        DateTime cutoff,
        int rosterLimit,
        int windowSeconds,
        CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(rosterLimit, 1, 500);
        var parameters = new
        {
            Cutoff = RepositoryMapping.ToDatabaseUtc(cutoff),
            Limit = boundedLimit,
        };
        const string countSql = """
            SELECT COUNT(*)
            FROM users
            WHERE is_active = 1 AND is_approved = 1 AND last_seen_at >= @Cutoff
            """;
        const string rosterSql = """
            SELECT u.id, u.display_name, r.name AS role, u.last_seen_at
            FROM users u
            INNER JOIN roles r ON r.id = u.role_id
            WHERE u.is_active = 1 AND u.is_approved = 1 AND u.last_seen_at >= @Cutoff
            ORDER BY u.last_seen_at DESC, u.id DESC
            LIMIT @Limit
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int count = await connection.ExecuteScalarAsync<int>(Command(
            countSql,
            parameters,
            cancellationToken)).ConfigureAwait(false);
        IEnumerable<PresenceRow> rows = await connection.QueryAsync<PresenceRow>(Command(
            rosterSql,
            parameters,
            cancellationToken)).ConfigureAwait(false);
        PresenceUserResponse[] online = rows.Select(static row => new PresenceUserResponse
        {
            Id = row.Id,
            DisplayName = row.DisplayName,
            Role = row.Role,
            LastSeenAt = RepositoryMapping.FromDatabaseUtc(row.LastSeenAt),
        }).ToArray();
        return new PresenceResponse
        {
            Online = online,
            Count = count,
            WindowSeconds = windowSeconds,
            Truncated = count > online.Length,
            RosterLimit = boundedLimit,
        };
    }

    public async Task TouchAsync(
        int userId,
        DateTime seenAt,
        DateTime staleBefore,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE users
            SET last_seen_at = @SeenAt,
                updated_at = GREATEST(updated_at, @SeenAt)
            WHERE id = @UserId
              AND is_active = 1
              AND is_approved = 1
              AND erased_at IS NULL
              AND (last_seen_at IS NULL OR last_seen_at < @StaleBefore)
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(sql, new
        {
            UserId = userId,
            SeenAt = RepositoryMapping.ToDatabaseUtc(seenAt),
            StaleBefore = RepositoryMapping.ToDatabaseUtc(staleBefore),
        }, cancellationToken)).ConfigureAwait(false);
    }

    private sealed class PresenceRow
    {
        public int Id { get; set; }
        public string? DisplayName { get; set; }
        public string Role { get; set; } = string.Empty;
        public DateTime? LastSeenAt { get; set; }
    }
}
