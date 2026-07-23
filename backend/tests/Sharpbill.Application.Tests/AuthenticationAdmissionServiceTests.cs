using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Identity;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;

namespace Sharpbill.Application.Tests;

public sealed class AuthenticationAdmissionServiceTests
{
    private static readonly DateTime Now =
        new(2026, 7, 22, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExistingIdentityRefreshesEvidenceAndUserActivityAsync()
    {
        var identityRepository = new IdentityRepositoryFake();
        var userRepository = new UserRepositoryFake();
        var roleRepository = Roles();
        UserIdentity storedIdentity = IdentityEntity(userId: 7);
        User storedUser = User(7, SystemRoleNames.DefaultUser, roleId: 2) with
        {
            Identities = [storedIdentity],
            LastLoginAt = Now.AddDays(-1),
            LastSeenAt = Now.AddDays(-1),
            UpdatedAt = Now.AddDays(-1),
        };
        identityRepository.Existing = storedIdentity;
        userRepository.Items[storedUser.Id] = storedUser;

        AuthenticationAdmissionResult result = await Service(
            identityRepository,
            userRepository,
            roleRepository).FindOrProvisionAsync(
            Settings(SignupMode.Open),
            VerifiedIdentity(hostedDomain: "new.example.test"),
            CancellationToken.None);

        Assert.False(result.WasCreated);
        Assert.Equal(Now, result.User.LastLoginAt);
        Assert.Equal(Now, result.User.LastSeenAt);
        Assert.Equal(Now, result.User.UpdatedAt);
        UserIdentity evidence = Assert.Single(identityRepository.EvidenceUpdates);
        Assert.Equal("new.example.test", evidence.ProviderHostedDomain);
        Assert.Equal(Now, evidence.UpdatedAt);
        Assert.Single(userRepository.Updates);
        Assert.Empty(userRepository.Added);
    }

    [Fact]
    public async Task ClosedSignupRejectsNonBootstrapWithoutWritesAsync()
    {
        var identityRepository = new IdentityRepositoryFake();
        var userRepository = new UserRepositoryFake();

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            Service(identityRepository, userRepository, Roles()).FindOrProvisionAsync(
                Settings(SignupMode.Closed),
                VerifiedIdentity(),
                CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("SIGNUP_CLOSED", exception.Code);
        Assert.Empty(userRepository.Added);
        Assert.Empty(identityRepository.Added);
    }

    [Fact]
    public async Task ApprovalSignupCreatesPendingUserUsingFallbackRoleAsync()
    {
        var identityRepository = new IdentityRepositoryFake();
        var userRepository = new UserRepositoryFake();

        AuthenticationAdmissionResult result = await Service(
            identityRepository,
            userRepository,
            Roles()).FindOrProvisionAsync(
            Settings(SignupMode.Approval, defaultRoleId: 99),
            VerifiedIdentity(),
            CancellationToken.None);

        Assert.True(result.WasCreated);
        Assert.False(result.User.IsApproved);
        Assert.True(result.User.IsActive);
        Assert.Equal(2, result.User.RoleId);
        Assert.Equal(SystemRoleNames.DefaultUser, result.User.RoleName);
        Assert.Equal(Now, result.User.CreatedAt);
        Assert.Single(userRepository.Added);
        UserIdentity identity = Assert.Single(identityRepository.Added);
        Assert.Equal(result.User.Id, identity.UserId);
        Assert.Equal(IdentityProvider.Google, identity.Provider);
    }

    [Fact]
    public async Task AdministratorBootstrapBypassesClosedSignupAsync()
    {
        var identityRepository = new IdentityRepositoryFake();
        var userRepository = new UserRepositoryFake();

        AuthenticationAdmissionResult result = await Service(
            identityRepository,
            userRepository,
            Roles()).FindOrProvisionAsync(
            Settings(SignupMode.Closed),
            VerifiedIdentity(subject: "google-admin"),
            CancellationToken.None);

        Assert.True(result.WasCreated);
        Assert.True(result.User.IsApproved);
        Assert.Equal(1, result.User.RoleId);
        Assert.Equal(SystemRoleNames.Administrator, result.User.RoleName);
    }

    private static AuthenticationAdmissionService Service(
        IIdentityRepository identities,
        IUserRepository users,
        IRoleRepository roles) =>
        new(
            identities,
            users,
            roles,
            new FixedClock(),
            new AuthenticationPolicy(new AuthenticationPolicyOptions
            {
                GoogleClientId = "google-client",
                GoogleAdminSubjects = new HashSet<string>(
                    ["google-admin"],
                    StringComparer.Ordinal),
            }));

    private static RoleRepositoryFake Roles()
    {
        var roles = new RoleRepositoryFake();
        roles.Items[1] = Role(1, SystemRoleNames.Administrator);
        roles.Items[2] = Role(2, SystemRoleNames.DefaultUser);
        return roles;
    }

    private static SiteSettings Settings(
        SignupMode signupMode,
        int defaultRoleId = 2) => new()
        {
            DefaultRoleId = defaultRoleId,
            SignupMode = signupMode,
            AllowGoogle = true,
        };

    private static VerifiedIdentity VerifiedIdentity(
        string subject = "google-subject",
        string? hostedDomain = null) => new()
        {
            Provider = ProviderContract.Google,
            Subject = subject,
            Email = "new.user@example.test",
            DisplayName = "New User",
            HostedDomain = hostedDomain,
        };

    private static UserIdentity IdentityEntity(int userId) => new()
    {
        Id = 19,
        UserId = userId,
        Provider = IdentityProvider.Google,
        ProviderSubject = "google-subject",
        CreatedAt = Now.AddDays(-30),
        UpdatedAt = Now.AddDays(-1),
    };

    private static User User(int id, string roleName, int roleId) => new()
    {
        Id = id,
        Email = "new.user@example.test",
        RoleId = roleId,
        RoleName = roleName,
        IsActive = true,
        IsApproved = true,
        CreatedAt = Now.AddDays(-30),
        UpdatedAt = Now.AddDays(-1),
    };

    private static Role Role(int id, string name) => new()
    {
        Id = id,
        Name = name,
        CreatedAt = Now,
        UpdatedAt = Now,
    };

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class IdentityRepositoryFake : IIdentityRepository
    {
        public UserIdentity? Existing { get; set; }

        public List<UserIdentity> Added { get; } = [];

        public List<UserIdentity> EvidenceUpdates { get; } = [];

        public Task<UserIdentity?> FindAsync(
            string provider,
            string providerNamespace,
            string providerSubject,
            bool forUpdate,
            CancellationToken cancellationToken) =>
            Task.FromResult(Existing);

        public Task<int> AddAsync(
            UserIdentity identity,
            CancellationToken cancellationToken)
        {
            UserIdentity added = identity with { Id = Added.Count + 1 };
            Added.Add(added);
            return Task.FromResult(added.Id);
        }

        public Task UpdateEvidenceAsync(
            UserIdentity identity,
            CancellationToken cancellationToken)
        {
            EvidenceUpdates.Add(identity);
            return Task.CompletedTask;
        }
    }

    private sealed class UserRepositoryFake : IUserRepository
    {
        public Dictionary<int, User> Items { get; } = [];

        public List<User> Added { get; } = [];

        public List<User> Updates { get; } = [];

        public Task<User?> FindAsync(
            int userId,
            bool forUpdate,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(userId));

        public Task<User?> FindForAuthenticationAsync(
            int userId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(userId));

        public Task<User?> FindByEmailAsync(
            string email,
            bool forUpdate,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.Values.SingleOrDefault(user =>
                string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<User?> FindByEmailForAuthenticationAsync(
            string email,
            CancellationToken cancellationToken) =>
            FindByEmailAsync(email, false, cancellationToken);

        public Task<(IReadOnlyList<User> Items, int Total)> ListAsync(
            UserQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<(IReadOnlyList<User>, int)>(([], 0));

        public Task<IReadOnlyList<User>> ListForExportAsync(
            UserQuery query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<User>>([]);

        public Task<int> CountActiveAdministratorsAsync(
            bool forUpdate,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> AddAsync(User user, CancellationToken cancellationToken)
        {
            int id = Items.Count == 0 ? 1 : Items.Keys.Max() + 1;
            User added = user with { Id = id };
            Items[id] = added;
            Added.Add(added);
            return Task.FromResult(id);
        }

        public Task UpdateAsync(User user, CancellationToken cancellationToken)
        {
            Items[user.Id] = user;
            Updates.Add(user);
            return Task.CompletedTask;
        }

        public Task ReplaceDirectPermissionsAsync(
            int userId,
            IReadOnlyCollection<int> permissionIds,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> ClearExpiredLocationsAsync(
            DateTime now,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<User>> ClaimDueForAnonymizationAsync(
            DateTime now,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<User>>([]);
    }

    private sealed class RoleRepositoryFake : IRoleRepository
    {
        public Dictionary<int, Role> Items { get; } = [];

        public Task<Role?> FindAsync(
            int roleId,
            bool forUpdate,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(roleId));

        public Task<Role?> FindByNameAsync(
            string name,
            bool forUpdate,
            CancellationToken cancellationToken) =>
            Task.FromResult(Items.Values.SingleOrDefault(role =>
                string.Equals(role.Name, name, StringComparison.Ordinal)));

        public Task<IReadOnlyList<Role>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Role>>(Items.Values.ToArray());

        public Task<IReadOnlyDictionary<int, int>> GetUserCountsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(
                new Dictionary<int, int>());

        public Task<int> AddAsync(Role role, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpdateAsync(Role role, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(int roleId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReplacePermissionsAsync(
            int roleId,
            IReadOnlyCollection<int> permissionIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
