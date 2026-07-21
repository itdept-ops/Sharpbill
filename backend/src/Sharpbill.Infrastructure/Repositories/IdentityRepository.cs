using Dapper;
using Sharpbill.Application.Abstractions;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class IdentityRepository(DatabaseSession session) : DapperRepository(session), IIdentityRepository
{
    private const string Columns = """
        id, user_id, provider, provider_namespace, provider_subject,
        provider_tenant_id, provider_hosted_domain, created_at, updated_at
        """;

    public async Task<UserIdentity?> FindAsync(
        string provider,
        string providerNamespace,
        string providerSubject,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT {Columns}
            FROM user_identities
            WHERE provider = @Provider
              AND provider_namespace = @ProviderNamespace
              AND provider_subject = @ProviderSubject
            LIMIT 1
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IdentityRow? row = await connection.QuerySingleOrDefaultAsync<IdentityRow>(Command(
            sql,
            new { Provider = provider, ProviderNamespace = providerNamespace, ProviderSubject = providerSubject },
            cancellationToken)).ConfigureAwait(false);
        return row is null ? null : RepositoryMapping.ToEntity(row);
    }

    public async Task<int> AddAsync(UserIdentity identity, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO user_identities
                (user_id, provider, provider_namespace, provider_subject,
                 provider_tenant_id, provider_hosted_domain, created_at, updated_at)
            VALUES
                (@UserId, @Provider, @ProviderNamespace, @ProviderSubject,
                 @ProviderTenantId, @ProviderHostedDomain, @CreatedAt, @UpdatedAt);
            SELECT LAST_INSERT_ID();
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<int>(Command(sql, new
        {
            identity.UserId,
            Provider = RepositoryMapping.Provider(identity.Provider),
            identity.ProviderNamespace,
            identity.ProviderSubject,
            identity.ProviderTenantId,
            identity.ProviderHostedDomain,
            CreatedAt = RepositoryMapping.ToDatabaseUtc(identity.CreatedAt),
            UpdatedAt = RepositoryMapping.ToDatabaseUtc(identity.UpdatedAt),
        }, cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateEvidenceAsync(UserIdentity identity, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE user_identities
            SET provider_tenant_id = @ProviderTenantId,
                provider_hosted_domain = @ProviderHostedDomain,
                updated_at = @UpdatedAt
            WHERE id = @Id
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(sql, new
        {
            identity.Id,
            identity.ProviderTenantId,
            identity.ProviderHostedDomain,
            UpdatedAt = RepositoryMapping.ToDatabaseUtc(identity.UpdatedAt),
        }, cancellationToken)).ConfigureAwait(false);
    }
}
