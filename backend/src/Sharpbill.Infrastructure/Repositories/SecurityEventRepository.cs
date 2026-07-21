using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class SecurityEventRepository(DatabaseSession session)
    : DapperRepository(session), ISecurityEventRepository
{
    private const string ResponseColumns = """
        e.id, e.event_type, e.outcome, e.severity, e.request_id, e.actor_user_id,
        e.target_type, e.target_id, e.source_ip, e.metadata AS metadata_json,
        e.occurred_at, e.retention_until, d.status AS delivery_status,
        d.attempts AS delivery_attempts, d.delivered_at
        """;

    public Task<long> AddWithPendingDeliveryAsync(
        SecurityEvent securityEvent,
        CancellationToken cancellationToken) =>
        Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            const string eventSql = """
                INSERT INTO security_events
                    (event_type, outcome, severity, request_id, actor_user_id, target_type,
                     target_id, source_ip, metadata, occurred_at, retention_until)
                VALUES
                    (@EventType, @Outcome, @Severity, @RequestId, @ActorUserId, @TargetType,
                     @TargetId, @SourceIp, @Metadata, @OccurredAt, @RetentionUntil)
                """;
            _ = await connection.ExecuteAsync(TransactionalCommand(eventSql, new
            {
                securityEvent.EventType,
                Outcome = RepositoryMapping.Outcome(securityEvent.Outcome),
                Severity = RepositoryMapping.Severity(securityEvent.Severity),
                securityEvent.RequestId,
                securityEvent.ActorUserId,
                securityEvent.TargetType,
                securityEvent.TargetId,
                securityEvent.SourceIp,
                Metadata = RepositoryMapping.Serialize(securityEvent.Metadata),
                OccurredAt = RepositoryMapping.ToDatabaseUtc(securityEvent.OccurredAt),
                RetentionUntil = RepositoryMapping.ToDatabaseUtc(securityEvent.RetentionUntil),
            }, transaction, token)).ConfigureAwait(false);
            long eventId = await connection.ExecuteScalarAsync<long>(TransactionalCommand(
                "SELECT LAST_INSERT_ID()",
                null,
                transaction,
                token)).ConfigureAwait(false);
            const string deliverySql = """
                INSERT INTO security_event_deliveries
                    (event_id, status, attempts, next_attempt_at)
                VALUES (@EventId, 'pending', 0, @OccurredAt)
                """;
            _ = await connection.ExecuteAsync(TransactionalCommand(deliverySql, new
            {
                EventId = eventId,
                OccurredAt = RepositoryMapping.ToDatabaseUtc(securityEvent.OccurredAt),
            }, transaction, token)).ConfigureAwait(false);
            return eventId;
        }, cancellationToken);

    public async Task<SecurityEventListResponse> ListAsync(
        SecurityEventQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        int limit = Math.Clamp(query.Limit, 1, 500);
        (string where, DynamicParameters parameters) = Filters(query);
        parameters.Add("Limit", limit + 1);
        string sql = $"""
            SELECT {ResponseColumns}
            FROM security_events e
            INNER JOIN security_event_deliveries d ON d.event_id = e.id
            WHERE {where}
            ORDER BY e.id DESC
            LIMIT @Limit
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<SecurityEventResponseRow> rows = (await connection.QueryAsync<SecurityEventResponseRow>(Command(
            sql,
            parameters,
            cancellationToken)).ConfigureAwait(false)).AsList();
        bool hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        SecurityEventResponse[] items = rows.Select(ToResponse).ToArray();
        return new SecurityEventListResponse
        {
            Items = items,
            NextCursor = hasMore && items.Length > 0 ? items[^1].Id : null,
        };
    }

    public async Task<IReadOnlyList<SecurityEventResponse>> ListForExportAsync(
        SecurityEventQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        int boundedLimit = Math.Clamp(limit, 1, 10_000);
        (string where, DynamicParameters parameters) = Filters(query);
        parameters.Add("Limit", boundedLimit);
        string sql = $"""
            SELECT {ResponseColumns}
            FROM security_events e
            INNER JOIN security_event_deliveries d ON d.event_id = e.id
            WHERE {where}
            ORDER BY e.id DESC
            LIMIT @Limit
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<SecurityEventResponseRow> rows =
            await connection.QueryAsync<SecurityEventResponseRow>(Command(
                sql,
                parameters,
                cancellationToken)).ConfigureAwait(false);
        return rows.Select(ToResponse).ToArray();
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
                FROM security_events
                WHERE retention_until <= @Cutoff
                ORDER BY retention_until, id
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
                "DELETE FROM security_events WHERE id IN @Ids",
                new { Ids = ids },
                transaction,
                token)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static (string Where, DynamicParameters Parameters) Filters(SecurityEventQuery query)
    {
        List<string> conditions = ["1 = 1"];
        DynamicParameters parameters = new();
        if (query.BeforeId is not null)
        {
            conditions.Add("e.id < @BeforeId");
            parameters.Add("BeforeId", query.BeforeId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            conditions.Add("e.event_type = @EventType");
            parameters.Add("EventType", query.EventType);
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            conditions.Add("e.outcome = @Outcome");
            parameters.Add("Outcome", query.Outcome);
        }

        if (!string.IsNullOrWhiteSpace(query.Severity))
        {
            conditions.Add("e.severity = @Severity");
            parameters.Add("Severity", query.Severity);
        }

        if (query.ActorUserId is not null)
        {
            conditions.Add("e.actor_user_id = @ActorUserId");
            parameters.Add("ActorUserId", query.ActorUserId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.RequestId))
        {
            conditions.Add("e.request_id = @RequestId");
            parameters.Add("RequestId", query.RequestId);
        }

        return (string.Join(" AND ", conditions), parameters);
    }

    private static SecurityEventResponse ToResponse(SecurityEventResponseRow row) => new()
    {
        Id = row.Id,
        EventType = row.EventType,
        Outcome = row.Outcome,
        Severity = row.Severity,
        RequestId = row.RequestId,
        ActorUserId = row.ActorUserId,
        TargetType = row.TargetType,
        TargetId = row.TargetId,
        SourceIp = row.SourceIp,
        Metadata = RepositoryMapping.DeserializeJsonElements(row.MetadataJson),
        OccurredAt = RepositoryMapping.FromDatabaseUtc(row.OccurredAt),
        RetentionUntil = RepositoryMapping.FromDatabaseUtc(row.RetentionUntil),
        DeliveryStatus = row.DeliveryStatus,
        DeliveryAttempts = row.DeliveryAttempts,
        DeliveredAt = RepositoryMapping.FromDatabaseUtc(row.DeliveredAt),
    };

    private sealed class SecurityEventResponseRow
    {
        public long Id { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string? RequestId { get; set; }
        public int? ActorUserId { get; set; }
        public string? TargetType { get; set; }
        public string? TargetId { get; set; }
        public string? SourceIp { get; set; }
        public string? MetadataJson { get; set; }
        public DateTime OccurredAt { get; set; }
        public DateTime RetentionUntil { get; set; }
        public string DeliveryStatus { get; set; } = string.Empty;
        public int DeliveryAttempts { get; set; }
        public DateTime? DeliveredAt { get; set; }
    }
}
