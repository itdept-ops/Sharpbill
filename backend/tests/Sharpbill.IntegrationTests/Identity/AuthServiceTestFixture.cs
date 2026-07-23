using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Identity;
using Sharpbill.Application.Validation;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Dashboard;
using Sharpbill.Contracts.Legal;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Services.Identity;
using Sharpbill.IntegrationTests.Business;

namespace Sharpbill.IntegrationTests.Identity;

internal sealed class AuthServiceTestFixture
{
    public static readonly DateTime Now =
        new(2026, 7, 22, 10, 30, 0, DateTimeKind.Utc);

    public AuthServiceTestFixture()
    {
        Settings.Value = CreateSettings();
        Roles.Items[1] = BusinessTestData.Role(
            1,
            SystemRoleNames.Administrator);
        Roles.Items[2] = BusinessTestData.Role(
            2,
            SystemRoleNames.DefaultUser);
        Verifier.Result = CreateVerifiedIdentity();
    }

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public FakeUserRepository Users { get; } = new();

    public FakeRoleRepository Roles { get; } = new();

    public FakeSettingsRepository Settings { get; } = new();

    public FakeClock Clock { get; } = new() { UtcNow = Now };

    public FakeRequestContextAccessor RequestContextAccessor { get; } = new();

    public AuthIdentityRepository Identities { get; } = new();

    public AuthSessionRepository Sessions { get; } = new();

    public AuthSecurityEventRepository SecurityEvents { get; } = new();

    public AuthLegalService Legal { get; } = new();

    public AuthTokenVerifier Verifier { get; } = new(ProviderContract.Google);

    public SharpbillOptions Configuration { get; } = new()
    {
        AppEnvironment = "local",
        Session = new SessionOptions
        {
            ActiveSecret = "auth-service-test-secret-0123456789abcdef0123456789abcdef",
            Issuer = "sharpbill",
            Audience = "sharpbill-web",
            LifetimeHours = 8,
            MaxActiveSessionsPerUser = 20,
        },
        Retention = new RetentionOptions
        {
            SecurityEventDays = 400,
            LegalAcceptanceDays = 2555,
        },
        IdentityProviders = new IdentityProviderOptions
        {
            GoogleClientId = "google-client",
            MicrosoftClientId = "microsoft-client",
        },
    };

    public RequestContext RequestContext { get; } = new()
    {
        RequestId = "auth-contract-request",
        ClientRequestId = "auth-contract-client",
        IpAddress = "203.0.113.20",
        UserAgent = "Sharpbill-auth-contract/1.0",
    };

    public AuthService CreateService()
    {
        IOptions<SharpbillOptions> options = Options.Create(Configuration);
        var policy = new AuthenticationPolicy(new AuthenticationPolicyOptions
        {
            IsLocal = Configuration.IsLocal,
            GoogleClientId = Configuration.IdentityProviders.GoogleClientId,
            MicrosoftClientId = Configuration.IdentityProviders.MicrosoftClientId,
            DevelopmentAuthenticationEnabled =
                DevelopmentAuthenticationGuard.IsEnabled(Configuration),
            GoogleAdminSubjects = Configuration.IdentityProviders.GoogleAdminSubjects,
            MicrosoftAdminTenantId =
                Configuration.IdentityProviders.MicrosoftAdminTenantId,
            MicrosoftAdminObjectIds =
                Configuration.IdentityProviders.MicrosoftAdminObjectIds,
            DevelopmentAdminEmails =
                Configuration.IdentityProviders.DevelopmentAdminEmails,
        });
        var sessionService = new SessionService(
            Sessions,
            Users,
            new AuthPresenceRepository(),
            SecurityEvents,
            Legal,
            UnitOfWork,
            Clock,
            RequestContextAccessor,
            new SessionJwtIssuer(options),
            options);
        var audit = new AuthenticationAuditService(
            SecurityEvents,
            UnitOfWork,
            Clock,
            options,
            NullLogger<AuthService>.Instance);
        var admission = new AuthenticationAdmissionService(
            Identities,
            Users,
            Roles,
            Clock,
            policy);
        return new AuthService(
            new AuthConfigurationService(Settings, policy),
            new ExternalLoginService(
                [Verifier],
                Settings,
                UnitOfWork,
                sessionService,
                Legal,
                new TokenLoginRequestValidator(),
                Clock,
                policy,
                admission,
                audit),
            new DevelopmentLoginService(
                Identities,
                Users,
                UnitOfWork,
                sessionService,
                Legal,
                new DevLoginRequestValidator(),
                Clock,
                policy,
                admission,
                audit,
                retryExecutor: null),
            new AuthAccountService(Users),
            new AuthSessionOperationsService(
                Sessions,
                SecurityEvents,
                UnitOfWork,
                Clock,
                options));
    }

    public static SiteSettings CreateSettings(
        bool allowGoogle = true,
        bool allowMicrosoft = true,
        SignupMode signupMode = SignupMode.Open,
        bool calmMode = false) => new()
        {
            Id = 1,
            DefaultRoleId = 2,
            AllowGoogle = allowGoogle,
            AllowMicrosoft = allowMicrosoft,
            SignupMode = signupMode,
            CalmMode = calmMode,
            UpdatedAt = Now,
        };

    public static VerifiedIdentity CreateVerifiedIdentity() => new()
    {
        Provider = ProviderContract.Google,
        Subject = "google-subject-7",
        Email = "existing@example.test",
        DisplayName = "Existing User",
        HostedDomain = "example.test",
    };

    public static User CreateUser(int userId = 7) => new()
    {
        Id = userId,
        Email = "existing@example.test",
        DisplayName = "Existing User",
        RoleId = 2,
        RoleName = SystemRoleNames.DefaultUser,
        IsActive = true,
        IsApproved = true,
        AccessVersion = 1,
        CreatedAt = Now.AddDays(-30),
        UpdatedAt = Now.AddDays(-1),
    };
}

internal sealed class AuthTokenVerifier(ProviderContract provider) : IIdentityTokenVerifier
{
    public ProviderContract Provider { get; } = provider;

    public VerifiedIdentity? Result { get; set; }

    public Exception? Failure { get; set; }

    public int Calls { get; private set; }

    public string? ExpectedNonce { get; private set; }

    public Task<VerifiedIdentity> VerifyAsync(
        string idToken,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        ExpectedNonce = expectedNonce;
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(Result ?? throw new InvalidOperationException("Verifier result is not configured"));
    }
}

internal sealed class AuthIdentityRepository : IIdentityRepository
{
    public UserIdentity? Existing { get; set; }

    public List<UserIdentity> Added { get; } = [];

    public List<UserIdentity> EvidenceUpdates { get; } = [];

    public Task<UserIdentity?> FindAsync(
        string provider,
        string providerNamespace,
        string providerSubject,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UserIdentity? result = Existing;
        if (result is null ||
            !string.Equals(result.Provider.ToString(), provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(result.ProviderNamespace, providerNamespace, StringComparison.Ordinal) ||
            !string.Equals(result.ProviderSubject, providerSubject, StringComparison.Ordinal))
        {
            result = null;
        }

        return Task.FromResult(result);
    }

    public Task<int> AddAsync(UserIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int id = Added.Count + 1;
        UserIdentity added = identity with { Id = id };
        Added.Add(added);
        Existing = added;
        return Task.FromResult(id);
    }

    public Task UpdateEvidenceAsync(UserIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EvidenceUpdates.Add(identity);
        Existing = identity;
        return Task.CompletedTask;
    }
}

internal sealed class AuthSessionRepository : ISessionRepository
{
    public Dictionary<Guid, UserSession> Items { get; } = [];

    public List<UserSession> Added { get; } = [];

    public List<int> RevokedSessionIds { get; } = [];

    public Task<UserSession?> FindByJtiAsync(
        Guid jti,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        Task.FromResult(Items.GetValueOrDefault(jti));

    public Task<UserSession?> FindByJtiForAuthenticationAsync(
        Guid jti,
        CancellationToken cancellationToken) =>
        Task.FromResult(Items.GetValueOrDefault(jti));

    public Task<UserSession?> FindAsync(
        int sessionId,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        Task.FromResult(Items.Values.SingleOrDefault(session => session.Id == sessionId));

    public Task<IReadOnlyList<UserSession>> ListActiveAsync(
        int userId,
        DateTime now,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserSession>>(Items.Values
            .Where(session =>
                session.UserId == userId &&
                session.RevokedAt is null &&
                session.ExpiresAt > now)
            .ToArray());

    public Task<int> CountActiveAsync(
        int userId,
        DateTime now,
        CancellationToken cancellationToken) =>
        Task.FromResult(Items.Values.Count(session =>
            session.UserId == userId &&
            session.RevokedAt is null &&
            session.ExpiresAt > now));

    public Task<int> AddAsync(UserSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int id = Items.Count + 1;
        UserSession added = session with { Id = id };
        Items[added.Jti] = added;
        Added.Add(added);
        return Task.FromResult(id);
    }

    public Task TouchAsync(
        int sessionId,
        DateTime seenAt,
        DateTime staleBefore,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RevokeAsync(
        int sessionId,
        DateTime revokedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UserSession session = Items.Values.Single(item => item.Id == sessionId);
        Items[session.Jti] = session with { RevokedAt = revokedAt };
        RevokedSessionIds.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task<int> RevokeAllAsync(
        int userId,
        DateTime revokedAt,
        CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (UserSession session in Items.Values.Where(item =>
                     item.UserId == userId && item.RevokedAt is null).ToArray())
        {
            Items[session.Jti] = session with { RevokedAt = revokedAt };
            RevokedSessionIds.Add(session.Id);
            count++;
        }

        return Task.FromResult(count);
    }

    public Task<int> PruneAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken) => Task.FromResult(0);
}

internal sealed class AuthSecurityEventRepository : ISecurityEventRepository
{
    public List<SecurityEvent> Added { get; } = [];

    public Task<long> AddWithPendingDeliveryAsync(
        SecurityEvent securityEvent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Added.Add(securityEvent);
        return Task.FromResult((long)Added.Count);
    }

    public Task<SecurityEventListResponse> ListAsync(
        SecurityEventQuery query,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SecurityEventListResponse());

    public Task<IReadOnlyList<SecurityEventResponse>> ListForExportAsync(
        SecurityEventQuery query,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SecurityEventResponse>>([]);

    public Task<int> PruneAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken) => Task.FromResult(0);
}

internal sealed class AuthLegalService : ILegalService
{
    public int RequireCalls { get; private set; }

    public List<int> RecordedUserIds { get; } = [];

    public LegalManifestResponse GetManifest() => throw new NotSupportedException();

    public void RequireCurrentAcceptance(bool accepted, string bundleVersion)
    {
        RequireCalls++;
    }

    public Task RecordAcceptanceAsync(
        int userId,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        RecordedUserIds.Add(userId);
        return Task.CompletedTask;
    }
}

internal sealed class AuthPresenceRepository : IPresenceRepository
{
    public Task<PresenceResponse> GetOnlineAsync(
        DateTime cutoff,
        int rosterLimit,
        int windowSeconds,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task TouchAsync(
        int userId,
        DateTime seenAt,
        DateTime staleBefore,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
