using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class HealthRepository(
    DatabaseSession session,
    IOptions<SharpbillOptions> options) : DapperRepository(session), IHealthRepository
{
    private readonly SharpbillOptions _options = options.Value;

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await connection.ExecuteScalarAsync<int>(Command(
                "SELECT 1",
                null,
                cancellationToken)).ConfigureAwait(false) == 1;
        }
        catch (MySqlException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task<IReadOnlySet<string>> GetSchemaHeadsAsync(
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<string> heads = await connection.QueryAsync<string>(Command(
            "SELECT version_num FROM alembic_version ORDER BY version_num",
            null,
            cancellationToken)).ConfigureAwait(false);
        return heads.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<bool> HasReachableAdministratorAsync(CancellationToken cancellationToken)
    {
        const string settingsSql = """
            SELECT allow_google, allow_microsoft
            FROM site_settings
            WHERE id = 1
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        ProviderSettingsRow? settings = await connection.QuerySingleOrDefaultAsync<ProviderSettingsRow>(Command(
            settingsSql,
            null,
            cancellationToken)).ConfigureAwait(false);
        if (settings is null)
        {
            return false;
        }

        bool google = settings.AllowGoogle &&
            !string.IsNullOrWhiteSpace(_options.IdentityProviders.GoogleClientId);
        bool microsoft = settings.AllowMicrosoft &&
            !string.IsNullOrWhiteSpace(_options.IdentityProviders.MicrosoftClientId);
        bool development = _options.IsLocal && _options.DevelopmentAuthentication.Enabled &&
            !string.IsNullOrWhiteSpace(_options.DevelopmentAuthentication.Secret);

        HashSet<string> effectiveProviders = new(StringComparer.Ordinal);
        if (google)
        {
            effectiveProviders.Add("google");
        }

        if (microsoft)
        {
            effectiveProviders.Add("microsoft");
        }

        if (development)
        {
            effectiveProviders.Add("dev");
        }

        if (effectiveProviders.Count == 0)
        {
            return false;
        }

        const string activeSql = """
            SELECT DISTINCT i.provider
            FROM user_identities i
            INNER JOIN users u ON u.id = i.user_id
            INNER JOIN roles r ON r.id = u.role_id
            WHERE r.name = 'admin'
              AND u.is_active = 1
              AND u.is_approved = 1
              AND (
                    (i.provider IN ('google', 'dev') AND i.provider_namespace = '')
                 OR (i.provider = 'microsoft' AND i.provider_tenant_id IS NOT NULL
                     AND i.provider_namespace = i.provider_tenant_id)
              )
            ORDER BY i.provider
            """;
        IEnumerable<string> activeProviders = await connection.QueryAsync<string>(Command(
            activeSql,
            null,
            cancellationToken)).ConfigureAwait(false);
        if (activeProviders.Any(effectiveProviders.Contains))
        {
            return true;
        }

        if (development)
        {
            return true;
        }

        if (google && await HasBootstrapPathAsync(
                "google",
                string.Empty,
                _options.IdentityProviders.GoogleAdminSubjects,
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        string? microsoftTenant = _options.IdentityProviders.MicrosoftAdminTenantId;
        return microsoft && !string.IsNullOrWhiteSpace(microsoftTenant) &&
            await HasBootstrapPathAsync(
                "microsoft",
                microsoftTenant,
                _options.IdentityProviders.MicrosoftAdminObjectIds,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasUnsafeAdministratorDefaultAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS(
                SELECT 1
                FROM site_settings s
                INNER JOIN roles r ON r.id = s.default_role_id
                WHERE s.id = 1 AND r.name = 'admin'
            )
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(Command(
            sql,
            null,
            cancellationToken)).ConfigureAwait(false);
    }

    private async Task<bool> HasBootstrapPathAsync(
        string provider,
        string providerNamespace,
        IReadOnlySet<string> subjects,
        CancellationToken cancellationToken)
    {
        if (subjects.Count == 0)
        {
            return false;
        }

        const string sql = """
            SELECT i.provider_subject, u.is_active, u.is_approved, r.name AS role_name
            FROM user_identities i
            INNER JOIN users u ON u.id = i.user_id
            INNER JOIN roles r ON r.id = u.role_id
            WHERE i.provider = @Provider
              AND i.provider_namespace = @ProviderNamespace
              AND i.provider_subject IN @Subjects
            ORDER BY i.provider_subject
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<BootstrapOwnerRow> rows = await connection.QueryAsync<BootstrapOwnerRow>(Command(
            sql,
            new
            {
                Provider = provider,
                ProviderNamespace = providerNamespace,
                Subjects = subjects.ToArray(),
            },
            cancellationToken)).ConfigureAwait(false);
        StringComparer subjectComparer = string.Equals(provider, "google", StringComparison.Ordinal)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        IReadOnlyDictionary<string, BootstrapOwnerRow> claimed = rows.ToDictionary(
            static row => row.ProviderSubject,
            subjectComparer);
        foreach (string subject in subjects)
        {
            if (!claimed.TryGetValue(subject, out BootstrapOwnerRow? owner))
            {
                return true;
            }

            if (owner.IsActive && owner.IsApproved &&
                string.Equals(owner.RoleName, "admin", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ProviderSettingsRow
    {
        public bool AllowGoogle { get; set; }
        public bool AllowMicrosoft { get; set; }
    }

    private sealed class BootstrapOwnerRow
    {
        public string ProviderSubject { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool IsApproved { get; set; }
        public string RoleName { get; set; } = string.Empty;
    }
}
