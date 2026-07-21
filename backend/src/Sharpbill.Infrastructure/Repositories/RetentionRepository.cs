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
}
