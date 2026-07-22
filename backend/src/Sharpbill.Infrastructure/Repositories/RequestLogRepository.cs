using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class RequestLogRepository(DatabaseSession session)
    : DapperRepository(session), IRequestLogRepository
{
    public async Task<RequestLogListResponse> ListAsync(
        RequestLogQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        int limit = Math.Clamp(query.Limit, 1, 500);
        List<string> baseConditions = ["1 = 1"];
        DynamicParameters parameters = new();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            baseConditions.Add("l.path LIKE @Search ESCAPE '\\\\'");
            parameters.Add("Search", $"{RepositoryMapping.EscapeLike(query.Search.Trim())}%");
        }

        if (!string.IsNullOrWhiteSpace(query.Method))
        {
            baseConditions.Add("l.method = @Method");
            parameters.Add("Method", query.Method.Trim().ToUpperInvariant());
        }

        if (query.UserId is not null)
        {
            baseConditions.Add("l.user_id = @UserId");
            parameters.Add("UserId", query.UserId.Value);
        }

        string baseWhere = string.Join(" AND ", baseConditions);
        string pageWhere = baseWhere;
        if (query.BeforeId is not null)
        {
            pageWhere += " AND l.id < @BeforeId";
            parameters.Add("BeforeId", query.BeforeId.Value);
        }

        parameters.Add("Limit", limit + 1);
        string pageSql = $"""
            SELECT l.id, l.method, l.path, l.user_id, u.email AS user_email,
                   l.ip, l.status_code, l.created_at
            FROM request_logs l
            LEFT JOIN users u ON u.id = l.user_id
            WHERE {pageWhere}
            ORDER BY l.id DESC
            LIMIT @Limit
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<RequestLogRow> rows = (await connection.QueryAsync<RequestLogRow>(Command(
            pageSql,
            parameters,
            cancellationToken)).ConfigureAwait(false)).AsList();
        bool hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        RequestLogResponse[] items = rows.Select(ToResponse).ToArray();
        long? total = null;
        if (query.IncludeTotal)
        {
            string countSql = $"SELECT COUNT(*) FROM request_logs l WHERE {baseWhere}";
            total = await connection.ExecuteScalarAsync<long>(Command(
                countSql,
                parameters,
                cancellationToken)).ConfigureAwait(false);
        }

        return new RequestLogListResponse
        {
            Items = items,
            Total = total,
            TotalIsExact = total.HasValue,
            NextCursor = hasMore && items.Length > 0 ? items[^1].Id : null,
        };
    }

    public async Task AddBatchAsync(
        IReadOnlyCollection<RequestLog> requestLogs,
        CancellationToken cancellationToken)
    {
        if (requestLogs.Count == 0)
        {
            return;
        }

        if (requestLogs.Count > 2_048)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestLogs),
                "A request-log persistence batch cannot exceed 2,048 records.");
        }

        await Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            const string sql = """
                INSERT INTO request_logs
                    (method, path, user_id, ip, status_code, created_at)
                VALUES (@Method, @Path, @UserId, @Ip, @StatusCode, @CreatedAt)
                """;
            object[] parameters = requestLogs.Select(log => new
            {
                log.Method,
                log.Path,
                log.UserId,
                Ip = log.IpAddress,
                log.StatusCode,
                CreatedAt = RepositoryMapping.ToDatabaseUtc(log.CreatedAt),
            }).Cast<object>().ToArray();
            _ = await connection.ExecuteAsync(TransactionalCommand(
                sql,
                parameters,
                transaction,
                token)).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(DateTime cutoff, int limit, CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(limit, 1, 10_000);
        return await Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            if (await RetentionSql.IsHoldActiveAsync(connection, transaction, token).ConfigureAwait(false))
            {
                return 0;
            }

            const string selectSql = """
                SELECT id
                FROM request_logs
                WHERE created_at <= @Cutoff
                ORDER BY created_at, id
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
                "DELETE FROM request_logs WHERE id IN @Ids",
                new { Ids = ids },
                transaction,
                token)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static RequestLogResponse ToResponse(RequestLogRow row) => new()
    {
        Id = row.Id,
        Method = row.Method,
        Path = row.Path,
        UserId = row.UserId,
        UserEmail = row.UserEmail,
        Ip = row.Ip,
        StatusCode = row.StatusCode,
        CreatedAt = RepositoryMapping.FromDatabaseUtc(row.CreatedAt),
    };

    private sealed class RequestLogRow
    {
        public long Id { get; set; }
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int? UserId { get; set; }
        public string? UserEmail { get; set; }
        public string? Ip { get; set; }
        public int StatusCode { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
