using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class LegalAcceptanceRepository(DatabaseSession session, IClock clock)
    : DapperRepository(session), ILegalAcceptanceRepository
{
    private const string Columns = """
        id, user_id, bundle_version, terms_version, eula_version, acceptable_use_version,
        privacy_version, terms_sha256, eula_sha256, acceptable_use_sha256, privacy_sha256,
        bundle_effective_date, acceptance_label, terms_action, eula_action,
        acceptable_use_action, privacy_action, accepted_at, retention_until, source_ip,
        user_agent, request_id, personal_data_erased_at
        """;

    public async Task<long> AddAsync(LegalAcceptance acceptance, CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO legal_acceptances
                (user_id, bundle_version, terms_version, eula_version, acceptable_use_version,
                 privacy_version, terms_sha256, eula_sha256, acceptable_use_sha256, privacy_sha256,
                 bundle_effective_date, acceptance_label, terms_action, eula_action,
                 acceptable_use_action, privacy_action, accepted_at, retention_until, source_ip,
                 user_agent, request_id, personal_data_erased_at)
            VALUES
                (@UserId, @BundleVersion, @TermsVersion, @EulaVersion, @AcceptableUseVersion,
                 @PrivacyVersion, @TermsSha256, @EulaSha256, @AcceptableUseSha256, @PrivacySha256,
                 @BundleEffectiveDate, @AcceptanceLabel, @TermsAction, @EulaAction,
                 @AcceptableUseAction, @PrivacyAction, @AcceptedAt, @RetentionUntil, @SourceIp,
                 @UserAgent, @RequestId, @PersonalDataErasedAt)
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(insertSql, Parameters(acceptance), cancellationToken))
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(Command(
            "SELECT LAST_INSERT_ID()",
            null,
            cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LegalAcceptance>> ListForUserAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT {Columns}
            FROM legal_acceptances
            WHERE user_id = @UserId
            ORDER BY accepted_at DESC, id DESC
            LIMIT 1000
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<LegalAcceptanceRow> rows = await connection.QueryAsync<LegalAcceptanceRow>(Command(
            sql,
            new { UserId = userId },
            cancellationToken)).ConfigureAwait(false);
        return rows.Select(ToEntity).ToArray();
    }

    public async Task<int> ErasePersonalDataAsync(
        int userId,
        DateTime erasedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE legal_acceptances
            SET source_ip = NULL,
                user_agent = NULL,
                request_id = NULL,
                personal_data_erased_at = @ErasedAt
            WHERE user_id = @UserId AND personal_data_erased_at IS NULL
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(Command(sql, new
        {
            UserId = userId,
            ErasedAt = RepositoryMapping.ToDatabaseUtc(erasedAt),
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
                FROM legal_acceptances
                WHERE retention_until <= @Now OR accepted_at <= @PolicyCutoff
                ORDER BY
                    CASE WHEN retention_until <= @Now THEN 0 ELSE 1 END,
                    CASE
                        WHEN retention_until <= @Now THEN retention_until
                        ELSE accepted_at
                    END,
                    id
                LIMIT @Limit
                FOR UPDATE SKIP LOCKED
                """;
            long[] ids = (await connection.QueryAsync<long>(TransactionalCommand(
                selectSql,
                new
                {
                    Now = RepositoryMapping.ToDatabaseUtc(clock.UtcNow),
                    PolicyCutoff = RepositoryMapping.ToDatabaseUtc(cutoff),
                    Limit = boundedLimit,
                },
                transaction,
                token)).ConfigureAwait(false)).AsList().ToArray();
            if (ids.Length == 0)
            {
                return 0;
            }

            return await connection.ExecuteAsync(TransactionalCommand(
                "DELETE FROM legal_acceptances WHERE id IN @Ids",
                new { Ids = ids },
                transaction,
                token)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static object Parameters(LegalAcceptance acceptance) => new
    {
        acceptance.UserId,
        acceptance.BundleVersion,
        acceptance.TermsVersion,
        acceptance.EulaVersion,
        acceptance.AcceptableUseVersion,
        acceptance.PrivacyVersion,
        acceptance.TermsSha256,
        acceptance.EulaSha256,
        acceptance.AcceptableUseSha256,
        acceptance.PrivacySha256,
        BundleEffectiveDate = RepositoryMapping.ToDatabaseDate(acceptance.BundleEffectiveDate),
        acceptance.AcceptanceLabel,
        TermsAction = RepositoryMapping.LegalAction(acceptance.TermsAction),
        EulaAction = RepositoryMapping.LegalAction(acceptance.EulaAction),
        AcceptableUseAction = RepositoryMapping.LegalAction(acceptance.AcceptableUseAction),
        PrivacyAction = RepositoryMapping.LegalAction(acceptance.PrivacyAction),
        AcceptedAt = RepositoryMapping.ToDatabaseUtc(acceptance.AcceptedAt),
        RetentionUntil = RepositoryMapping.ToDatabaseUtc(acceptance.RetentionUntil),
        acceptance.SourceIp,
        acceptance.UserAgent,
        acceptance.RequestId,
        PersonalDataErasedAt = acceptance.PersonalDataErasedAt is null
            ? (DateTime?)null
            : RepositoryMapping.ToDatabaseUtc(acceptance.PersonalDataErasedAt.Value),
    };

    private static LegalAcceptance ToEntity(LegalAcceptanceRow row) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        BundleVersion = row.BundleVersion,
        TermsVersion = row.TermsVersion,
        EulaVersion = row.EulaVersion,
        AcceptableUseVersion = row.AcceptableUseVersion,
        PrivacyVersion = row.PrivacyVersion,
        TermsSha256 = row.TermsSha256,
        EulaSha256 = row.EulaSha256,
        AcceptableUseSha256 = row.AcceptableUseSha256,
        PrivacySha256 = row.PrivacySha256,
        BundleEffectiveDate = DateOnly.FromDateTime(row.BundleEffectiveDate),
        AcceptanceLabel = row.AcceptanceLabel,
        TermsAction = RepositoryMapping.LegalAction(row.TermsAction),
        EulaAction = RepositoryMapping.LegalAction(row.EulaAction),
        AcceptableUseAction = RepositoryMapping.LegalAction(row.AcceptableUseAction),
        PrivacyAction = RepositoryMapping.LegalAction(row.PrivacyAction),
        AcceptedAt = RepositoryMapping.FromDatabaseUtc(row.AcceptedAt),
        RetentionUntil = RepositoryMapping.FromDatabaseUtc(row.RetentionUntil),
        SourceIp = row.SourceIp,
        UserAgent = row.UserAgent,
        RequestId = row.RequestId,
        PersonalDataErasedAt = RepositoryMapping.FromDatabaseUtc(row.PersonalDataErasedAt),
    };

    private sealed class LegalAcceptanceRow
    {
        public long Id { get; set; }
        public int UserId { get; set; }
        public string BundleVersion { get; set; } = string.Empty;
        public string TermsVersion { get; set; } = string.Empty;
        public string EulaVersion { get; set; } = string.Empty;
        public string AcceptableUseVersion { get; set; } = string.Empty;
        public string PrivacyVersion { get; set; } = string.Empty;
        public string TermsSha256 { get; set; } = string.Empty;
        public string EulaSha256 { get; set; } = string.Empty;
        public string AcceptableUseSha256 { get; set; } = string.Empty;
        public string PrivacySha256 { get; set; } = string.Empty;
        public DateTime BundleEffectiveDate { get; set; }
        public string AcceptanceLabel { get; set; } = string.Empty;
        public string TermsAction { get; set; } = string.Empty;
        public string EulaAction { get; set; } = string.Empty;
        public string AcceptableUseAction { get; set; } = string.Empty;
        public string PrivacyAction { get; set; } = string.Empty;
        public DateTime AcceptedAt { get; set; }
        public DateTime RetentionUntil { get; set; }
        public string? SourceIp { get; set; }
        public string? UserAgent { get; set; }
        public string? RequestId { get; set; }
        public DateTime? PersonalDataErasedAt { get; set; }
    }
}
