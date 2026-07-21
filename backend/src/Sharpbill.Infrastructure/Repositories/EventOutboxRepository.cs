using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class EventOutboxRepository(DatabaseSession session)
    : DapperRepository(session), IEventOutboxRepository
{
    public Task<IReadOnlyList<EventDeliveryEnvelope>> ClaimAsync(
        string workerId,
        int limit,
        DateTime now,
        DateTime leaseExpiresAt,
        CancellationToken cancellationToken)
    {
        string owner = NormalizeWorkerId(workerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        if (leaseExpiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseExpiresAt),
                "The delivery lease must expire after the claim time.");
        }

        return Session.ExecuteTransactionallyAsync<IReadOnlyList<EventDeliveryEnvelope>>(
            async (connection, transaction, token) =>
            {
                const string claimSql = """
                    SELECT event_id
                    FROM security_event_deliveries
                    WHERE ((status IN ('pending', 'retry') AND next_attempt_at <= @Now)
                        OR (status = 'leased' AND lease_expires_at <= @Now))
                    ORDER BY event_id
                    LIMIT @Limit
                    FOR UPDATE SKIP LOCKED
                    """;
                long[] ids = (await connection.QueryAsync<long>(TransactionalCommand(
                    claimSql,
                    new { Now = RepositoryMapping.ToDatabaseUtc(now), Limit = limit },
                    transaction,
                    token)).ConfigureAwait(false)).AsList().ToArray();
                if (ids.Length == 0)
                {
                    return [];
                }

                const string leaseSql = """
                    UPDATE security_event_deliveries
                    SET status = 'leased',
                        lease_owner = @Owner,
                        lease_expires_at = @LeaseExpiresAt
                    WHERE event_id IN @Ids
                    """;
                _ = await connection.ExecuteAsync(TransactionalCommand(leaseSql, new
                {
                    Owner = owner,
                    LeaseExpiresAt = RepositoryMapping.ToDatabaseUtc(leaseExpiresAt),
                    Ids = ids,
                }, transaction, token)).ConfigureAwait(false);

                const string eventSql = """
                    SELECT id AS event_id, event_type, outcome, severity, request_id,
                           actor_user_id, target_type, target_id, source_ip,
                           metadata AS metadata_json, occurred_at
                    FROM security_events
                    WHERE id IN @Ids
                    ORDER BY id
                    """;
                IEnumerable<EventEnvelopeRow> rows = await connection.QueryAsync<EventEnvelopeRow>(
                    TransactionalCommand(
                        eventSql,
                        new { Ids = ids },
                        transaction,
                        token)).ConfigureAwait(false);
                return rows.Select(ToEnvelope).ToArray();
            },
            cancellationToken);
    }

    public async Task<bool> MarkDeliveredAsync(
        long eventId,
        string workerId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE security_event_deliveries
            SET status = 'delivered',
                attempts = attempts + 1,
                last_attempt_at = @Now,
                delivered_at = @Now,
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_error = NULL
            WHERE event_id = @EventId
              AND status = 'leased'
              AND lease_owner = @WorkerId
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(Command(sql, new
        {
            EventId = eventId,
            WorkerId = NormalizeWorkerId(workerId),
            Now = RepositoryMapping.ToDatabaseUtc(now),
        }, cancellationToken)).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task<bool> MarkFailedAsync(
        long eventId,
        string workerId,
        DateTime now,
        DateTime nextAttemptAt,
        string errorFingerprint,
        int maxAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorFingerprint);
        if (nextAttemptAt < now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAttemptAt),
                "The next delivery attempt cannot precede the failure time.");
        }

        const string sql = """
            UPDATE security_event_deliveries
            SET status = CASE
                    WHEN attempts + 1 >= @MaxAttempts THEN 'dead_letter'
                    ELSE 'retry'
                END,
                last_attempt_at = @Now,
                next_attempt_at = TIMESTAMPADD(
                    SECOND,
                    LEAST(3600, CAST(POWER(2, LEAST(attempts + 1, 12)) AS UNSIGNED)),
                    @Now),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_error = @ErrorFingerprint,
                attempts = attempts + 1
            WHERE event_id = @EventId
              AND status = 'leased'
              AND lease_owner = @WorkerId
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(Command(sql, new
        {
            EventId = eventId,
            WorkerId = NormalizeWorkerId(workerId),
            Now = RepositoryMapping.ToDatabaseUtc(now),
            ErrorFingerprint = errorFingerprint[..Math.Min(errorFingerprint.Length, 255)],
            MaxAttempts = maxAttempts,
        }, cancellationToken)).ConfigureAwait(false);
        return affected == 1;
    }

    private static string NormalizeWorkerId(string workerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        string value = workerId.Trim();
        return value[..Math.Min(value.Length, 64)];
    }

    private static EventDeliveryEnvelope ToEnvelope(EventEnvelopeRow row) => new()
    {
        EventId = row.EventId,
        EventType = row.EventType,
        Outcome = row.Outcome,
        Severity = row.Severity,
        RequestId = row.RequestId,
        ActorUserId = row.ActorUserId,
        TargetType = row.TargetType,
        TargetId = row.TargetId,
        SourceIp = row.SourceIp,
        Metadata = RepositoryMapping.DeserializeObjects(row.MetadataJson),
        OccurredAt = RepositoryMapping.FromDatabaseUtc(row.OccurredAt),
    };

    private sealed class EventEnvelopeRow
    {
        public long EventId { get; set; }
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
    }
}
