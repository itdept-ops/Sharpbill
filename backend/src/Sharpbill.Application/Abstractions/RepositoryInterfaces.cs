using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Dashboard;
using Sharpbill.Contracts.Health;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Application.Abstractions;

public interface IUnitOfWork : IAsyncDisposable
{
    Task BeginAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IIdentityRepository
{
    Task<UserIdentity?> FindAsync(
        string provider,
        string providerNamespace,
        string providerSubject,
        bool forUpdate,
        CancellationToken cancellationToken);
    Task<int> AddAsync(UserIdentity identity, CancellationToken cancellationToken);
    Task UpdateEvidenceAsync(UserIdentity identity, CancellationToken cancellationToken);
}

public interface INonceRepository
{
    Task<int> CountActiveAsync(DateTime now, CancellationToken cancellationToken);
    Task AddAsync(LoginNonce nonce, CancellationToken cancellationToken);
    Task<bool> ConsumeAsync(string nonce, DateTime now, CancellationToken cancellationToken);
    Task<int> PruneExpiredAsync(DateTime now, int limit, CancellationToken cancellationToken);
}

public interface ISessionRepository
{
    Task<UserSession?> FindByJtiAsync(Guid jti, bool forUpdate, CancellationToken cancellationToken);
    Task<UserSession?> FindByJtiForAuthenticationAsync(
        Guid jti,
        CancellationToken cancellationToken);
    Task<UserSession?> FindAsync(int sessionId, bool forUpdate, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserSession>> ListActiveAsync(
        int userId,
        DateTime now,
        CancellationToken cancellationToken);
    Task<int> CountActiveAsync(int userId, DateTime now, CancellationToken cancellationToken);
    Task<int> AddAsync(UserSession session, CancellationToken cancellationToken);
    Task TouchAsync(
        int sessionId,
        DateTime seenAt,
        DateTime staleBefore,
        CancellationToken cancellationToken);
    Task RevokeAsync(int sessionId, DateTime revokedAt, CancellationToken cancellationToken);
    Task<int> RevokeAllAsync(int userId, DateTime revokedAt, CancellationToken cancellationToken);
    Task<int> PruneAsync(DateTime cutoff, int limit, CancellationToken cancellationToken);
}

public interface ILegalAcceptanceRepository
{
    Task<long> AddAsync(LegalAcceptance acceptance, CancellationToken cancellationToken);
    Task<IReadOnlyList<LegalAcceptance>> ListForUserAsync(
        int userId,
        CancellationToken cancellationToken);
    Task<int> ErasePersonalDataAsync(
        int userId,
        DateTime erasedAt,
        CancellationToken cancellationToken);
    Task<int> PruneAsync(DateTime cutoff, int limit, CancellationToken cancellationToken);
}

public interface IUserRepository
{
    Task<User?> FindAsync(int userId, bool forUpdate, CancellationToken cancellationToken);
    Task<User?> FindForAuthenticationAsync(int userId, CancellationToken cancellationToken);
    Task<User?> FindByEmailAsync(string email, bool forUpdate, CancellationToken cancellationToken);
    Task<User?> FindByEmailForAuthenticationAsync(string email, CancellationToken cancellationToken);
    Task<(IReadOnlyList<User> Items, int Total)> ListAsync(
        UserQuery query,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<User>> ListForExportAsync(
        UserQuery query,
        int limit,
        CancellationToken cancellationToken);
    Task<int> CountActiveAdministratorsAsync(bool forUpdate, CancellationToken cancellationToken);
    Task<int> AddAsync(User user, CancellationToken cancellationToken);
    Task UpdateAsync(User user, CancellationToken cancellationToken);
    Task ReplaceDirectPermissionsAsync(
        int userId,
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken);
    Task<int> ClearExpiredLocationsAsync(DateTime now, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<User>> ClaimDueForAnonymizationAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken);
}

public interface IRoleRepository
{
    Task<Role?> FindAsync(int roleId, bool forUpdate, CancellationToken cancellationToken);
    Task<Role?> FindByNameAsync(string name, bool forUpdate, CancellationToken cancellationToken);
    Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<int, int>> GetUserCountsAsync(CancellationToken cancellationToken);
    Task<int> AddAsync(Role role, CancellationToken cancellationToken);
    Task UpdateAsync(Role role, CancellationToken cancellationToken);
    Task DeleteAsync(int roleId, CancellationToken cancellationToken);
    Task ReplacePermissionsAsync(
        int roleId,
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken);
}

public interface IPermissionRepository
{
    Task<Permission?> FindByKeyAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<Permission>> FindByKeysAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<Permission>> ListAsync(CancellationToken cancellationToken);
    Task<int> AddAsync(Permission permission, CancellationToken cancellationToken);
}

public interface ISettingsRepository
{
    Task<SiteSettings?> GetAsync(bool forUpdate, CancellationToken cancellationToken);
    Task<SiteSettings?> GetForShareAsync(CancellationToken cancellationToken);
    Task UpdateAsync(SiteSettings settings, CancellationToken cancellationToken);
}

public interface ISecurityEventRepository
{
    Task<long> AddWithPendingDeliveryAsync(
        SecurityEvent securityEvent,
        CancellationToken cancellationToken);
    Task<SecurityEventListResponse> ListAsync(
        SecurityEventQuery query,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SecurityEventResponse>> ListForExportAsync(
        SecurityEventQuery query,
        int limit,
        CancellationToken cancellationToken);
    Task<int> PruneAsync(DateTime cutoff, int limit, CancellationToken cancellationToken);
}

public interface IEventOutboxRepository
{
    Task<IReadOnlyList<EventDeliveryEnvelope>> ClaimAsync(
        string workerId,
        int limit,
        DateTime now,
        DateTime leaseExpiresAt,
        CancellationToken cancellationToken);
    Task<bool> MarkDeliveredAsync(
        long eventId,
        string workerId,
        DateTime now,
        CancellationToken cancellationToken);
    Task<bool> MarkFailedAsync(
        long eventId,
        string workerId,
        DateTime now,
        DateTime nextAttemptAt,
        string errorFingerprint,
        int maxAttempts,
        CancellationToken cancellationToken);
}

public interface IRequestLogRepository
{
    Task<RequestLogListResponse> ListAsync(
        RequestLogQuery query,
        CancellationToken cancellationToken);
    Task AddBatchAsync(
        IReadOnlyCollection<RequestLog> requestLogs,
        CancellationToken cancellationToken);
    Task<int> PruneAsync(DateTime cutoff, int limit, CancellationToken cancellationToken);
}

public interface IPresenceRepository
{
    Task<PresenceResponse> GetOnlineAsync(
        DateTime cutoff,
        int rosterLimit,
        int windowSeconds,
        CancellationToken cancellationToken);
    Task TouchAsync(
        int userId,
        DateTime seenAt,
        DateTime staleBefore,
        CancellationToken cancellationToken);
}

public interface IDashboardRepository
{
    Task<DashboardResponse> GetAsync(DateTime onlineCutoff, CancellationToken cancellationToken);
    Task<AnalyticsResponse> GetAnalyticsAsync(
        DateTime onlineCutoff,
        DateOnly signupsSince,
        CancellationToken cancellationToken);
}

public interface IHealthRepository
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> GetSchemaHeadsAsync(CancellationToken cancellationToken);
    Task<bool> HasReachableAdministratorAsync(CancellationToken cancellationToken);
    Task<bool> HasUnsafeAdministratorDefaultAsync(CancellationToken cancellationToken);
}

public interface IRetentionRepository
{
    Task<bool> IsHoldActiveAsync(bool forUpdate, CancellationToken cancellationToken);
    Task<int> AnonymizeDueAccountsAsync(DateTime now, int limit, CancellationToken cancellationToken);
}
