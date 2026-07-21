using System.Data.Common;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Dashboard;
using Sharpbill.Contracts.Health;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.IntegrationTests.Business;

public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Begins { get; private set; }
    public int Commits { get; private set; }
    public int Rollbacks { get; private set; }

    public Task BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Begins++;
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Commits++;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        Rollbacks++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeUserRepository : IUserRepository
{
    public Dictionary<int, User> Items { get; } = [];
    public List<User> Updates { get; } = [];
    public IReadOnlyList<User>? ExportItems { get; set; }
    public int? ActiveAdministratorCount { get; set; }
    public HashSet<int> DatabaseFailureUserIds { get; } = [];

    public Task<User?> FindAsync(
        int userId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Items.GetValueOrDefault(userId));
    }

    public Task<User?> FindForAuthenticationAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Items.GetValueOrDefault(userId));
    }

    public Task<User?> FindByEmailAsync(
        string email,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Items.Values.FirstOrDefault(user =>
            string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<User?> FindByEmailForAuthenticationAsync(
        string email,
        CancellationToken cancellationToken) =>
        FindByEmailAsync(email, false, cancellationToken);

    public Task<(IReadOnlyList<User> Items, int Total)> ListAsync(
        UserQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<User> users = Items.Values
            .OrderBy(static user => user.CreatedAt)
            .ThenBy(static user => user.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
        return Task.FromResult((users, Items.Count));
    }

    public Task<IReadOnlyList<User>> ListForExportAsync(
        UserQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ExportItems ?? Items.Values.Take(limit).ToArray());
    }

    public Task<int> CountActiveAdministratorsAsync(
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = ActiveAdministratorCount ?? Items.Values.Count(user =>
            string.Equals(user.RoleName, SystemRoleNames.Administrator, StringComparison.Ordinal) &&
            user.IsActive &&
            user.IsApproved);
        return Task.FromResult(count);
    }

    public Task<int> AddAsync(User user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int id = Items.Count == 0 ? 1 : Items.Keys.Max() + 1;
        Items[id] = user with { Id = id };
        return Task.FromResult(id);
    }

    public Task UpdateAsync(User user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DatabaseFailureUserIds.Contains(user.Id))
        {
            throw new FakeDatabaseException();
        }

        Items[user.Id] = user;
        Updates.Add(user);
        return Task.CompletedTask;
    }

    public Task ReplaceDirectPermissionsAsync(
        int userId,
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<int> ClearExpiredLocationsAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<IReadOnlyList<User>> ClaimDueForAnonymizationAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<User>>([]);
}

public sealed class FakeRoleRepository : IRoleRepository
{
    public Dictionary<int, Role> Items { get; } = [];
    public Dictionary<int, int> UserCounts { get; } = [];

    public Task<Role?> FindAsync(
        int roleId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Items.GetValueOrDefault(roleId));
    }

    public Task<Role?> FindByNameAsync(
        string name,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Items.Values.FirstOrDefault(role =>
            string.Equals(role.Name, name, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Role>>(Items.Values.OrderBy(static role => role.Id).ToArray());

    public Task<IReadOnlyDictionary<int, int>> GetUserCountsAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, int>>(UserCounts);

    public Task<int> AddAsync(Role role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int id = Items.Count == 0 ? 1 : Items.Keys.Max() + 1;
        Items[id] = role with { Id = id };
        return Task.FromResult(id);
    }

    public Task UpdateAsync(Role role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Items[role.Id] = role;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(int roleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Items.Remove(roleId);
        return Task.CompletedTask;
    }

    public Task ReplacePermissionsAsync(
        int roleId,
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class FakePermissionRepository : IPermissionRepository
{
    public Dictionary<int, Permission> Items { get; } = [];

    public Task<Permission?> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Items.Values.FirstOrDefault(permission =>
            string.Equals(permission.Key, key, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<Permission>> FindByKeysAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<Permission> permissions = Items.Values
            .Where(permission => keys.Contains(permission.Key, StringComparer.Ordinal))
            .ToArray();
        return Task.FromResult(permissions);
    }

    public Task<IReadOnlyList<Permission>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Permission>>(Items.Values.ToArray());

    public Task<int> AddAsync(Permission permission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int id = Items.Count == 0 ? 1 : Items.Keys.Max() + 1;
        Items[id] = permission with { Id = id };
        return Task.FromResult(id);
    }
}

public sealed class FakeSettingsRepository : ISettingsRepository
{
    public SiteSettings? Value { get; set; }

    public Task<SiteSettings?> GetAsync(bool forUpdate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Value);
    }

    public Task<SiteSettings?> GetForShareAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Value);
    }

    public Task UpdateAsync(SiteSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Value = settings;
        return Task.CompletedTask;
    }
}

public sealed class FakeHealthRepository : IHealthRepository
{
    public bool ReachableAdministrator { get; set; } = true;

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<IReadOnlySet<string>> GetSchemaHeadsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal) { "0021" });

    public Task<bool> HasReachableAdministratorAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ReachableAdministrator);

    public Task<bool> HasUnsafeAdministratorDefaultAsync(CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

public sealed class FakeSessionService : ISessionService
{
    public List<int> RevokedUserIds { get; } = [];

    public Task<SessionToken> StartAsync(
        int userId,
        bool legalAccepted,
        string legalBundleVersion,
        RequestContext context,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<SessionResponse>> ListAsync(
        int actorUserId,
        int targetUserId,
        bool includeDeviceDetails,
        Guid? currentJti,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SessionResponse>>([]);

    public Task RevokeAsync(
        int actorUserId,
        int targetUserId,
        int sessionId,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RevokeAllAsync(
        int targetUserId,
        DateTime validAfter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RevokedUserIds.Add(targetUserId);
        return Task.CompletedTask;
    }

    public Task<SessionValidationResult> ValidateAsync(
        int userId,
        Guid jti,
        DateTime issuedAt,
        CancellationToken cancellationToken) => Task.FromResult(
            SessionValidationResult.Invalid("SESSION_REVOKED", "This session was signed out"));
}

public sealed class FakeSecurityEventService : ISecurityEventService
{
    public List<SecurityEventWrite> Writes { get; } = [];

    public Task<long> RecordAsync(
        SecurityEventWrite securityEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add(securityEvent);
        return Task.FromResult((long)Writes.Count);
    }

    public Task<SecurityEventListResponse> ListAsync(
        SecurityEventQuery query,
        int actorUserId,
        CancellationToken cancellationToken) => Task.FromResult(new SecurityEventListResponse());

    public Task<ExportDocument> ExportAsync(
        SecurityEventQuery query,
        int actorUserId,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}

public sealed class FakeGeoService : IGeoService
{
    public GeoPlace Place { get; set; } = new("Hillsboro", "America/Los_Angeles");

    public GeoPlace Resolve(double latitude, double longitude) => Place;
}

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);
}

public sealed class FakeRequestContextAccessor : IRequestContextAccessor
{
    public RequestContext Current { get; set; } = new()
    {
        RequestId = "business-test",
        IpAddress = "203.0.113.1",
    };
}

public sealed class FakeDatabaseException : DbException;

public static class BusinessTestData
{
    public static readonly DateTime Timestamp = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

    public static Permission Permission(int id, string key) => new()
    {
        Id = id,
        Key = key,
        IsSystem = true,
        CreatedAt = Timestamp,
        UpdatedAt = Timestamp,
    };

    public static Role Role(
        int id,
        string name,
        params Permission[] permissions) => new()
        {
            Id = id,
            Name = name,
            IsSystem = name is SystemRoleNames.Administrator or SystemRoleNames.DefaultUser,
            Version = 1,
            CreatedAt = Timestamp,
            UpdatedAt = Timestamp,
            Permissions = permissions,
        };

    public static User User(int id, string roleName, IEnumerable<string> permissions) => new()
    {
        Id = id,
        Email = $"user{id}@example.com",
        DisplayName = $"User {id}",
        RoleId = roleName == SystemRoleNames.Administrator ? 1 : 2,
        RoleName = roleName,
        RolePermissionKeys = permissions.ToHashSet(StringComparer.Ordinal),
        IsActive = true,
        IsApproved = true,
        AccessVersion = 1,
        CreatedAt = Timestamp,
        UpdatedAt = Timestamp,
    };

    public static SiteSettings Settings() => new()
    {
        Id = 1,
        DefaultRoleId = 2,
        AllowGoogle = true,
        AllowMicrosoft = true,
        UpdatedAt = Timestamp,
    };

    public static SharpbillOptions Options() => new()
    {
        AppEnvironment = "local",
        IdentityProviders = new IdentityProviderOptions
        {
            GoogleClientId = "google-client",
            MicrosoftClientId = "microsoft-client",
        },
    };

    public static IOptions<SharpbillOptions> WrappedOptions(SharpbillOptions? options = null) =>
        Microsoft.Extensions.Options.Options.Create(options ?? Options());
}
