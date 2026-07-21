using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class SettingsRepository(DatabaseSession session)
    : DapperRepository(session), ISettingsRepository
{
    public async Task<SiteSettings?> GetForShareAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, signup_mode, allow_google, allow_microsoft, default_role_id,
                   calm_mode, retention_hold, retention_hold_reference, updated_at
            FROM site_settings
            WHERE id = 1
            FOR SHARE
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SettingsRow? row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(Command(
            sql,
            null,
            cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToEntity(row);
    }

    public async Task<SiteSettings?> GetAsync(bool forUpdate, CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT id, signup_mode, allow_google, allow_microsoft, default_role_id,
                   calm_mode, retention_hold, retention_hold_reference, updated_at
            FROM site_settings
            WHERE id = 1
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        SettingsRow? row = await connection.QuerySingleOrDefaultAsync<SettingsRow>(Command(
            sql,
            null,
            cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToEntity(row);
    }

    public async Task UpdateAsync(SiteSettings settings, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE site_settings
            SET signup_mode = @SignupMode,
                allow_google = @AllowGoogle,
                allow_microsoft = @AllowMicrosoft,
                default_role_id = @DefaultRoleId,
                calm_mode = @CalmMode,
                retention_hold = @RetentionHold,
                retention_hold_reference = @RetentionHoldReference,
                updated_at = @UpdatedAt
            WHERE id = 1
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(sql, new
        {
            SignupMode = RepositoryMapping.SignupMode(settings.SignupMode),
            settings.AllowGoogle,
            settings.AllowMicrosoft,
            settings.DefaultRoleId,
            settings.CalmMode,
            settings.RetentionHold,
            settings.RetentionHoldReference,
            UpdatedAt = RepositoryMapping.ToDatabaseUtc(settings.UpdatedAt),
        }, cancellationToken)).ConfigureAwait(false);
    }

    private static SiteSettings ToEntity(SettingsRow row) => new()
    {
        Id = row.Id,
        SignupMode = RepositoryMapping.SignupMode(row.SignupMode),
        AllowGoogle = row.AllowGoogle,
        AllowMicrosoft = row.AllowMicrosoft,
        DefaultRoleId = row.DefaultRoleId,
        CalmMode = row.CalmMode,
        RetentionHold = row.RetentionHold,
        RetentionHoldReference = row.RetentionHoldReference,
        UpdatedAt = RepositoryMapping.FromDatabaseUtc(row.UpdatedAt),
    };

    private sealed class SettingsRow
    {
        public int Id { get; set; }
        public string SignupMode { get; set; } = string.Empty;
        public bool AllowGoogle { get; set; }
        public bool AllowMicrosoft { get; set; }
        public int DefaultRoleId { get; set; }
        public bool CalmMode { get; set; }
        public bool RetentionHold { get; set; }
        public string? RetentionHoldReference { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
