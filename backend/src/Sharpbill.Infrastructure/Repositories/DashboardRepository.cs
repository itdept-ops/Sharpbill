using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Dashboard;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class DashboardRepository(DatabaseSession session)
    : DapperRepository(session), IDashboardRepository
{
    public async Task<DashboardResponse> GetAsync(
        DateTime onlineCutoff,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COUNT(*) AS total_users,
                COALESCE(SUM(is_active = 1 AND is_approved = 1), 0) AS active_users,
                COALESCE(SUM(is_active = 1 AND is_approved = 1 AND last_seen_at >= @OnlineCutoff), 0)
                    AS online_users
            FROM users
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        DashboardRow row = await connection.QuerySingleAsync<DashboardRow>(Command(sql, new
        {
            OnlineCutoff = RepositoryMapping.ToDatabaseUtc(onlineCutoff),
        }, cancellationToken)).ConfigureAwait(false);
        return new DashboardResponse
        {
            Stats = new DashboardStats
            {
                TotalUsers = row.TotalUsers,
                ActiveUsers = row.ActiveUsers,
                OnlineUsers = row.OnlineUsers,
            },
        };
    }

    public async Task<AnalyticsResponse> GetAnalyticsAsync(
        DateTime onlineCutoff,
        DateOnly signupsSince,
        CancellationToken cancellationToken)
    {
        const string rolesSql = """
            SELECT r.name, COUNT(u.id) AS count
            FROM roles r
            LEFT JOIN users u ON u.role_id = r.id
            GROUP BY r.id, r.name
            ORDER BY count DESC, r.name
            """;
        const string providersSql = """
            SELECT provider AS name, COUNT(DISTINCT user_id) AS count
            FROM user_identities
            GROUP BY provider
            ORDER BY provider
            """;
        const string signupsSql = """
            SELECT DATE(created_at) AS signup_date, COUNT(*) AS count
            FROM users
            WHERE created_at >= @SignupsSince
            GROUP BY DATE(created_at)
            ORDER BY signup_date
            """;
        const string statusSql = """
            SELECT
                COUNT(*) AS total,
                COALESCE(SUM(is_active = 1 AND is_approved = 1), 0) AS active,
                COALESCE(SUM(is_approved = 0), 0) AS pending,
                COALESCE(SUM(is_active = 0 AND is_approved = 1), 0) AS disabled,
                COALESCE(SUM(is_active = 1 AND is_approved = 1 AND last_seen_at >= @OnlineCutoff), 0)
                    AS online
            FROM users
            """;
        var parameters = new
        {
            OnlineCutoff = RepositoryMapping.ToDatabaseUtc(onlineCutoff),
            SignupsSince = signupsSince.ToDateTime(TimeOnly.MinValue),
        };
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        NamedCountRow[] roleRows = (await connection.QueryAsync<NamedCountRow>(Command(
            rolesSql,
            null,
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        NamedCountRow[] providerRows = (await connection.QueryAsync<NamedCountRow>(Command(
            providersSql,
            null,
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        SignupRow[] signupRows = (await connection.QueryAsync<SignupRow>(Command(
            signupsSql,
            parameters,
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        StatusRow status = await connection.QuerySingleAsync<StatusRow>(Command(
            statusSql,
            parameters,
            cancellationToken)).ConfigureAwait(false);

        IReadOnlyDictionary<DateOnly, int> signupCounts = signupRows.ToDictionary(
            static row => DateOnly.FromDateTime(row.SignupDate),
            static row => row.Count);
        DateOnly today = signupsSince.AddDays(13);

        var signups = new List<SignupCount>();
        for (DateOnly date = signupsSince; date <= today; date = date.AddDays(1))
        {
            signups.Add(new SignupCount
            {
                Date = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Count = signupCounts.GetValueOrDefault(date),
            });
        }

        return new AnalyticsResponse
        {
            Roles = roleRows.Select(static row => new NamedCount
            {
                Role = row.Name,
                Count = row.Count,
            }).ToArray(),
            Providers = providerRows.Select(static row => new NamedCount
            {
                Provider = row.Name,
                Count = row.Count,
            }).ToArray(),
            Signups = signups,
            Status = new StatusCounts
            {
                Total = status.Total,
                Active = status.Active,
                Pending = status.Pending,
                Disabled = status.Disabled,
                Online = status.Online,
            },
        };
    }

    private sealed class DashboardRow
    {
        public int TotalUsers { get; set; }
        public int ActiveUsers { get; set; }
        public int OnlineUsers { get; set; }
    }

    private sealed class NamedCountRow
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class SignupRow
    {
        public DateTime SignupDate { get; set; }
        public int Count { get; set; }
    }

    private sealed class StatusRow
    {
        public int Total { get; set; }
        public int Active { get; set; }
        public int Pending { get; set; }
        public int Disabled { get; set; }
        public int Online { get; set; }
    }
}
