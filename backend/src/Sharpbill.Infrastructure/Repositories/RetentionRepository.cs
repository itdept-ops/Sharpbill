using Dapper;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class RetentionRepository(
    DatabaseSession session,
    IOptions<SharpbillOptions> options) : DapperRepository(session), IRetentionRepository
{
    private readonly RetentionOptions _retention = options.Value.Retention;

    public async Task<bool> IsHoldActiveAsync(
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT retention_hold
            FROM site_settings
            WHERE id = 1
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        bool? hold = await connection.QuerySingleOrDefaultAsync<bool?>(Command(
            sql,
            null,
            cancellationToken)).ConfigureAwait(false);
        return hold ?? throw new InvalidOperationException(
            "Site settings are missing; retention cannot make a safe hold decision.");
    }

    public async Task<RetentionBacklogSnapshot> GetBacklogAsync(
        DateTime capturedAt,
        CancellationToken cancellationToken)
    {
        DateTime requestLogCutoff = capturedAt.AddDays(-_retention.RequestLogDays);
        DateTime sessionCutoff = capturedAt.AddDays(-_retention.SessionDays);
        DateTime locationCutoff = capturedAt.AddHours(-_retention.PreciseLocationHours);
        DateTime pendingCutoff = capturedAt.AddDays(-_retention.PendingAccountDays);
        DateTime disabledCutoff = capturedAt.AddDays(-_retention.DisabledAccountDays);
        DateTime legalAcceptanceCutoff = capturedAt.AddDays(-_retention.LegalAcceptanceDays);
        const string sql = """
            SELECT
                s.retention_hold AS hold_active,
                (SELECT COUNT(*) FROM login_nonces WHERE expires_at <= @Now)
                    AS nonces_due_count,
                (SELECT MIN(expires_at) FROM login_nonces WHERE expires_at <= @Now)
                    AS nonces_oldest_expiry,
                (SELECT COUNT(*) FROM request_logs WHERE created_at <= @RequestLogCutoff)
                    AS request_logs_due_count,
                (SELECT MIN(created_at) FROM request_logs WHERE created_at <= @RequestLogCutoff)
                    AS request_logs_oldest_created,
                (SELECT COUNT(*) FROM user_sessions
                    WHERE expires_at <= @SessionCutoff OR revoked_at <= @SessionCutoff)
                    AS sessions_due_count,
                (SELECT MIN(LEAST(expires_at, COALESCE(revoked_at, expires_at)))
                    FROM user_sessions
                    WHERE expires_at <= @SessionCutoff OR revoked_at <= @SessionCutoff)
                    AS sessions_oldest_end,
                (SELECT COUNT(*) FROM users
                    WHERE location_retention_until <= @Now
                       OR last_location_at <= @LocationCutoff
                       OR (last_location_at IS NULL AND
                           (last_latitude IS NOT NULL OR last_longitude IS NOT NULL OR
                            last_location_accuracy IS NOT NULL)))
                    AS precise_locations_due_count,
                (SELECT MIN(location_retention_until) FROM users
                    WHERE location_retention_until <= @Now)
                    AS precise_locations_oldest_deadline,
                (SELECT MIN(last_location_at) FROM users
                    WHERE last_location_at <= @LocationCutoff)
                    AS precise_locations_oldest_capture,
                (SELECT MIN(updated_at) FROM users
                    WHERE last_location_at IS NULL AND
                         (last_latitude IS NOT NULL OR last_longitude IS NOT NULL OR
                          last_location_accuracy IS NOT NULL))
                    AS precise_locations_oldest_malformed_update,
                (SELECT COUNT(*) FROM users u
                    INNER JOIN roles r ON r.id = u.role_id
                    WHERE u.erased_at IS NULL
                      AND r.name <> 'admin'
                      AND (u.erasure_due_at <= @Now
                           OR (u.is_approved = 0 AND u.created_at <= @PendingCutoff)
                           OR (u.is_active = 0 AND u.deactivated_at IS NOT NULL
                               AND u.deactivated_at <= @DisabledCutoff)))
                    AS accounts_due_count,
                (SELECT MIN(u.erasure_due_at) FROM users u
                    INNER JOIN roles r ON r.id = u.role_id
                    WHERE u.erased_at IS NULL AND r.name <> 'admin'
                      AND u.erasure_due_at <= @Now)
                    AS accounts_oldest_erasure_due,
                (SELECT MIN(u.created_at) FROM users u
                    INNER JOIN roles r ON r.id = u.role_id
                    WHERE u.erased_at IS NULL AND r.name <> 'admin'
                      AND u.is_approved = 0 AND u.created_at <= @PendingCutoff)
                    AS accounts_oldest_pending_created,
                (SELECT MIN(u.deactivated_at) FROM users u
                    INNER JOIN roles r ON r.id = u.role_id
                    WHERE u.erased_at IS NULL AND r.name <> 'admin'
                      AND u.is_active = 0 AND u.deactivated_at IS NOT NULL
                      AND u.deactivated_at <= @DisabledCutoff)
                    AS accounts_oldest_deactivated,
                (SELECT COUNT(*) FROM security_events WHERE retention_until <= @Now)
                    AS security_events_due_count,
                (SELECT MIN(retention_until) FROM security_events WHERE retention_until <= @Now)
                    AS security_events_oldest_deadline,
                (SELECT COUNT(*) FROM legal_acceptances
                    WHERE retention_until <= @Now OR accepted_at <= @LegalAcceptanceCutoff)
                    AS legal_acceptances_due_count,
                (SELECT MIN(retention_until) FROM legal_acceptances WHERE retention_until <= @Now)
                    AS legal_acceptances_oldest_deadline,
                (SELECT MIN(accepted_at) FROM legal_acceptances
                    WHERE accepted_at <= @LegalAcceptanceCutoff)
                    AS legal_acceptances_oldest_accepted
            FROM site_settings s
            WHERE s.id = 1
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        RetentionBacklogRow? row = await connection.QuerySingleOrDefaultAsync<RetentionBacklogRow>(Command(
            sql,
            new
            {
                Now = RepositoryMapping.ToDatabaseUtc(capturedAt),
                RequestLogCutoff = RepositoryMapping.ToDatabaseUtc(requestLogCutoff),
                SessionCutoff = RepositoryMapping.ToDatabaseUtc(sessionCutoff),
                LocationCutoff = RepositoryMapping.ToDatabaseUtc(locationCutoff),
                PendingCutoff = RepositoryMapping.ToDatabaseUtc(pendingCutoff),
                DisabledCutoff = RepositoryMapping.ToDatabaseUtc(disabledCutoff),
                LegalAcceptanceCutoff = RepositoryMapping.ToDatabaseUtc(legalAcceptanceCutoff),
            },
            cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            throw new InvalidOperationException(
                "Site settings are missing; retention backlog cannot make a safe hold decision.");
        }

        DateTime utcCapturedAt = Normalize(capturedAt);
        return new RetentionBacklogSnapshot(
            utcCapturedAt,
            row.HoldActive,
            [
                Category("nonces", false, row.NoncesDueCount, row.NoncesOldestExpiry),
                Category(
                    "request_logs",
                    true,
                    row.RequestLogsDueCount,
                    AddDays(row.RequestLogsOldestCreated, _retention.RequestLogDays)),
                Category(
                    "sessions",
                    true,
                    row.SessionsDueCount,
                    AddDays(row.SessionsOldestEnd, _retention.SessionDays)),
                Category(
                    "precise_locations",
                    true,
                    row.PreciseLocationsDueCount,
                    Earliest(
                        row.PreciseLocationsOldestDeadline,
                        AddHours(
                            row.PreciseLocationsOldestCapture,
                            _retention.PreciseLocationHours),
                        row.PreciseLocationsOldestMalformedUpdate)),
                Category(
                    "accounts",
                    true,
                    row.AccountsDueCount,
                    Earliest(
                        row.AccountsOldestErasureDue,
                        AddDays(
                            row.AccountsOldestPendingCreated,
                            _retention.PendingAccountDays),
                        AddDays(
                            row.AccountsOldestDeactivated,
                            _retention.DisabledAccountDays))),
                Category(
                    "security_events",
                    true,
                    row.SecurityEventsDueCount,
                    row.SecurityEventsOldestDeadline),
                Category(
                    "legal_acceptances",
                    true,
                    row.LegalAcceptancesDueCount,
                    Earliest(
                        row.LegalAcceptancesOldestDeadline,
                        AddDays(
                            row.LegalAcceptancesOldestAccepted,
                            _retention.LegalAcceptanceDays))),
            ]);
    }

    public async Task<int> AnonymizeDueAccountsAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(limit, 1, 1_000);
        DateTime pendingCutoff = now.AddDays(-_retention.PendingAccountDays);
        DateTime disabledCutoff = now.AddDays(-_retention.DisabledAccountDays);
        return await Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            if (await RetentionSql.IsHoldActiveAsync(connection, transaction, token).ConfigureAwait(false))
            {
                return 0;
            }

            const string roleSql = """
                SELECT id
                FROM roles
                WHERE name = 'user'
                LIMIT 1
                FOR UPDATE
                """;
            int? defaultRoleId = await connection.QuerySingleOrDefaultAsync<int?>(TransactionalCommand(
                roleSql,
                null,
                transaction,
                token)).ConfigureAwait(false);
            if (defaultRoleId is null)
            {
                throw new InvalidOperationException(
                    "The built-in user role is missing; account erasure was refused.");
            }

            const string dueSql = """
                SELECT u.id, u.erasure_due_at, u.is_approved, u.created_at
                FROM users u
                INNER JOIN roles r ON r.id = u.role_id
                WHERE u.erased_at IS NULL
                  AND r.name <> 'admin'
                  AND (
                        u.erasure_due_at <= @Now
                     OR (u.is_approved = 0 AND u.created_at <= @PendingCutoff)
                     OR (u.is_active = 0 AND u.deactivated_at IS NOT NULL
                         AND u.deactivated_at <= @DisabledCutoff)
                  )
                ORDER BY
                    CASE
                        WHEN u.erasure_due_at <= @Now THEN 0
                        WHEN u.is_approved = 0 AND u.created_at <= @PendingCutoff THEN 1
                        ELSE 2
                    END,
                    u.id
                LIMIT @Limit
                FOR UPDATE SKIP LOCKED
                """;
            AccountDueRow[] rows = (await connection.QueryAsync<AccountDueRow>(TransactionalCommand(
                dueSql,
                new
                {
                    Now = RepositoryMapping.ToDatabaseUtc(now),
                    PendingCutoff = RepositoryMapping.ToDatabaseUtc(pendingCutoff),
                    DisabledCutoff = RepositoryMapping.ToDatabaseUtc(disabledCutoff),
                    Limit = boundedLimit,
                },
                transaction,
                token)).ConfigureAwait(false)).AsList().ToArray();
            foreach (AccountDueRow row in rows)
            {
                string trigger = row.ErasureDueAt is not null && row.ErasureDueAt <=
                    RepositoryMapping.ToDatabaseUtc(now)
                    ? "requested_erasure_due"
                    : !row.IsApproved && row.CreatedAt <= RepositoryMapping.ToDatabaseUtc(pendingCutoff)
                        ? "pending_account_expired"
                        : "disabled_account_expired";
                await AnonymizeAsync(
                    connection,
                    transaction,
                    row.Id,
                    defaultRoleId.Value,
                    now,
                    trigger,
                    token).ConfigureAwait(false);
            }

            return rows.Length;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task AnonymizeAsync(
        MySqlConnector.MySqlConnection connection,
        MySqlConnector.MySqlTransaction transaction,
        int userId,
        int defaultRoleId,
        DateTime now,
        string trigger,
        CancellationToken cancellationToken)
    {
        const string updateUserSql = """
            UPDATE users
            SET email = CONCAT('erased-', id, '@privacy.invalid'),
                display_name = NULL,
                title = NULL,
                department = NULL,
                phone = NULL,
                location = NULL,
                timezone = NULL,
                bio = NULL,
                accent_color = NULL,
                ui_prefs = NULL,
                last_login_at = NULL,
                last_seen_at = NULL,
                last_latitude = NULL,
                last_longitude = NULL,
                last_location_accuracy = NULL,
                last_location_at = NULL,
                location_retention_until = NULL,
                is_active = 0,
                is_approved = 0,
                session_valid_after = @Now,
                role_id = @DefaultRoleId,
                access_version = access_version + 1,
                deactivated_at = COALESCE(deactivated_at, @Now),
                erasure_requested_at = NULL,
                erasure_due_at = NULL,
                erased_at = @Now,
                updated_at = @Now
            WHERE id = @UserId AND erased_at IS NULL
            """;
        var parameters = new
        {
            UserId = userId,
            DefaultRoleId = defaultRoleId,
            Now = RepositoryMapping.ToDatabaseUtc(now),
        };
        int affected = await connection.ExecuteAsync(TransactionalCommand(
            updateUserSql,
            parameters,
            transaction,
            cancellationToken)).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"The claimed account {userId} could not be anonymized consistently.");
        }

        _ = await connection.ExecuteAsync(TransactionalCommand(
            "DELETE FROM user_permissions WHERE user_id = @UserId",
            parameters,
            transaction,
            cancellationToken)).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(TransactionalCommand(
            "DELETE FROM user_sessions WHERE user_id = @UserId",
            parameters,
            transaction,
            cancellationToken)).ConfigureAwait(false);
        const string evidenceSql = """
            UPDATE legal_acceptances
            SET source_ip = NULL,
                user_agent = NULL,
                request_id = NULL,
                personal_data_erased_at = @Now
            WHERE user_id = @UserId AND personal_data_erased_at IS NULL
            """;
        _ = await connection.ExecuteAsync(TransactionalCommand(
            evidenceSql,
            parameters,
            transaction,
            cancellationToken)).ConfigureAwait(false);

        const string eventSql = """
            INSERT INTO security_events
                (event_type, outcome, severity, actor_user_id, target_type, target_id,
                 metadata, occurred_at, retention_until)
            VALUES
                ('privacy.account.erased', 'success', 'info', NULL, 'user', @TargetId,
                 @Metadata, @Now, @RetentionUntil)
            """;
        _ = await connection.ExecuteAsync(TransactionalCommand(eventSql, new
        {
            TargetId = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Metadata = RepositoryMapping.Serialize(new Dictionary<string, object?>
            {
                ["policy_trigger"] = trigger,
            }),
            Now = RepositoryMapping.ToDatabaseUtc(now),
            RetentionUntil = RepositoryMapping.ToDatabaseUtc(now.AddDays(_retention.SecurityEventDays)),
        }, transaction, cancellationToken)).ConfigureAwait(false);
        long eventId = await connection.ExecuteScalarAsync<long>(TransactionalCommand(
            "SELECT LAST_INSERT_ID()",
            null,
            transaction,
            cancellationToken)).ConfigureAwait(false);
        const string deliverySql = """
            INSERT INTO security_event_deliveries
                (event_id, status, attempts, next_attempt_at)
            VALUES (@EventId, 'pending', 0, @Now)
            """;
        _ = await connection.ExecuteAsync(TransactionalCommand(deliverySql, new
        {
            EventId = eventId,
            Now = RepositoryMapping.ToDatabaseUtc(now),
        }, transaction, cancellationToken)).ConfigureAwait(false);
    }

    private sealed class AccountDueRow
    {
        public int Id { get; set; }
        public DateTime? ErasureDueAt { get; set; }
        public bool IsApproved { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    private static RetentionBacklogCategory Category(
        string category,
        bool governedByHold,
        long dueCount,
        DateTime? oldestEligibleAt) => new(
            category,
            governedByHold,
            dueCount,
            RepositoryMapping.FromDatabaseUtc(oldestEligibleAt));

    private static DateTime? AddDays(DateTime? value, int days) =>
        value?.AddDays(days);

    private static DateTime? AddHours(DateTime? value, int hours) =>
        value?.AddHours(hours);

    private static DateTime? Earliest(params DateTime?[] values) => values
        .Where(static value => value.HasValue)
        .Min();

    private static DateTime Normalize(DateTime value) =>
        DateTime.SpecifyKind(RepositoryMapping.ToDatabaseUtc(value), DateTimeKind.Utc);

    private sealed class RetentionBacklogRow
    {
        public bool HoldActive { get; set; }
        public long NoncesDueCount { get; set; }
        public DateTime? NoncesOldestExpiry { get; set; }
        public long RequestLogsDueCount { get; set; }
        public DateTime? RequestLogsOldestCreated { get; set; }
        public long SessionsDueCount { get; set; }
        public DateTime? SessionsOldestEnd { get; set; }
        public long PreciseLocationsDueCount { get; set; }
        public DateTime? PreciseLocationsOldestDeadline { get; set; }
        public DateTime? PreciseLocationsOldestCapture { get; set; }
        public DateTime? PreciseLocationsOldestMalformedUpdate { get; set; }
        public long AccountsDueCount { get; set; }
        public DateTime? AccountsOldestErasureDue { get; set; }
        public DateTime? AccountsOldestPendingCreated { get; set; }
        public DateTime? AccountsOldestDeactivated { get; set; }
        public long SecurityEventsDueCount { get; set; }
        public DateTime? SecurityEventsOldestDeadline { get; set; }
        public long LegalAcceptancesDueCount { get; set; }
        public DateTime? LegalAcceptancesOldestDeadline { get; set; }
        public DateTime? LegalAcceptancesOldestAccepted { get; set; }
    }
}
