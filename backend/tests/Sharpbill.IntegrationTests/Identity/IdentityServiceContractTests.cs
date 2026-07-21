using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Legal;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.Legal;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Services.Identity;

namespace Sharpbill.IntegrationTests.Identity;

public sealed class IdentityServiceContractTests
{
    private static readonly DateTime FixedNow = new(2026, 7, 21, 12, 34, 56, DateTimeKind.Utc);

    [Fact]
    public void SessionIssuerPinsTheJwtContractAndDerivedKeyId()
    {
        SharpbillOptions options = CreateOptions();
        var issuer = new SessionJwtIssuer(Options.Create(options));
        Guid jti = Guid.Parse("dc031ee7-3f54-4e94-8f0f-80f411f13e89");

        SessionToken result = issuer.Issue(42, jti, FixedNow);
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(result.Value);
        string expectedKeyId = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(options.Session.ActiveSecret)))[..16];

        Assert.Equal("HS256", token.Header.Alg);
        Assert.Equal("JWT", token.Header.Typ);
        Assert.Equal(expectedKeyId, token.Header.Kid);
        Assert.Equal("42", token.Subject);
        Assert.Equal(jti.ToString("N"), token.Id);
        Assert.Equal(options.Session.Issuer, token.Issuer);
        Assert.Contains(options.Session.Audience, token.Audiences);
        Assert.Equal("session", token.Payload["token_type"]);
        Assert.True(token.Payload.ContainsKey(JwtRegisteredClaimNames.Iat));
        Assert.Equal(FixedNow.AddHours(8), result.ExpiresAt);
    }

    [Fact]
    public async Task LegalServiceStagesImmutableEvidenceAndOutboxEventAsync()
    {
        var evidenceRepository = new CapturingLegalRepository();
        var eventRepository = new CapturingSecurityEventRepository();
        var service = new LegalService(
            evidenceRepository,
            eventRepository,
            new FixedClock(FixedNow),
            Options.Create(CreateOptions()));
        var context = new RequestContext
        {
            RequestId = new string('r', 80),
            IpAddress = "203.0.113.10",
            UserAgent = new string('u', 450),
        };

        await service.RecordAcceptanceAsync(7, context, CancellationToken.None);

        LegalAcceptance evidence = Assert.Single(evidenceRepository.Added);
        Assert.Equal(LegalBundleV3.BundleVersion, evidence.BundleVersion);
        Assert.Equal(LegalBundleV3.TermsSha256, evidence.TermsSha256);
        Assert.Equal(LegalBundleV3.PrivacySha256, evidence.PrivacySha256);
        Assert.Equal(FixedNow, evidence.AcceptedAt);
        Assert.Equal(FixedNow.AddDays(2555), evidence.RetentionUntil);
        Assert.Equal(64, evidence.RequestId?.Length);
        Assert.Equal(400, evidence.UserAgent?.Length);

        SecurityEvent securityEvent = Assert.Single(eventRepository.Added);
        Assert.Equal("legal.accepted", securityEvent.EventType);
        Assert.Equal(LegalBundleV3.BundleVersion, securityEvent.TargetId);
        Assert.Equal(LegalBundleV3.AcceptanceLabel, securityEvent.Metadata["acceptance_label"]);
        Assert.Equal("acknowledgement", securityEvent.Metadata["privacy_action"]);
    }

    [Fact]
    public async Task NonceServiceIssuesAndConsumesExactlyOnceAsync()
    {
        var repository = new InMemoryNonceRepository();
        var unitOfWork = new TrackingUnitOfWork();
        var service = new NonceService(
            repository,
            new StubSettingsRepository(),
            unitOfWork,
            new FixedClock(FixedNow),
            Options.Create(CreateOptions()),
            NullLogger<NonceService>.Instance);

        NonceResponse issued = await service.IssueAsync(CancellationToken.None);

        Assert.True(issued.Nonce.Length > 20);
        Assert.DoesNotContain('=', issued.Nonce);
        Assert.True(await service.ConsumeAsync(issued.Nonce, CancellationToken.None));
        Assert.False(await service.ConsumeAsync(issued.Nonce, CancellationToken.None));
        Assert.Equal(3, unitOfWork.Commits);
        Assert.Equal(0, unitOfWork.Rollbacks);
    }

    [Fact]
    public async Task NonceServiceFailsClosedAtSharedCapacityAsync()
    {
        var repository = new InMemoryNonceRepository();
        for (int index = 0; index < 5_000; index++)
        {
            await repository.AddAsync(
                new LoginNonce
                {
                    Nonce = $"pending-{index}",
                    CreatedAt = FixedNow,
                    ExpiresAt = FixedNow.AddMinutes(5),
                },
                CancellationToken.None);
        }

        var service = new NonceService(
            repository,
            new StubSettingsRepository(),
            new TrackingUnitOfWork(),
            new FixedClock(FixedNow),
            Options.Create(CreateOptions()),
            NullLogger<NonceService>.Instance);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(
            () => service.IssueAsync(CancellationToken.None));

        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("LOGIN_STATE_CAPACITY", exception.Code);
        Assert.Equal("30", exception.Headers["Retry-After"]);
    }

    [Fact]
    public async Task ProviderRuntimeFailsFastAtVerificationCapacityAndRejectsReplayAsync()
    {
        SharpbillOptions options = CreateOptions();
        options.IdentityProviders.VerificationMaxConcurrency = 1;
        using var runtime = new ProviderVerificationRuntime(
            Options.Create(options),
            new FixedClock(FixedNow));
        using IDisposable first = await runtime.AcquireVerificationAsync(CancellationToken.None);

        await Assert.ThrowsAsync<IdentityProviderUnavailableException>(async () =>
        {
            using IDisposable ignored = await runtime.AcquireVerificationAsync(CancellationToken.None);
        });
        Assert.False(runtime.IsReplay("signed-token", FixedNow.AddMinutes(5)));
        Assert.True(runtime.IsReplay("signed-token", FixedNow.AddMinutes(5)));
    }

    [Fact]
    public void ProviderReplayCacheCapacityNeverRejectsAnUnrelatedToken()
    {
        using var runtime = new ProviderVerificationRuntime(
            Options.Create(CreateOptions()),
            new FixedClock(FixedNow));
        DateTime expiry = FixedNow.AddMinutes(5);

        for (int index = 0; index < 10_001; index++)
        {
            Assert.False(runtime.IsReplay($"unique-signed-token-{index}", expiry));
        }

        Assert.True(runtime.IsReplay("unique-signed-token-10000", expiry));
    }

    [Fact]
    public void GoogleMultiAudienceTokensRequireThisClientAsAuthorizedParty()
    {
        const string clientId = "sharpbill.apps.googleusercontent.com";
        string[] multipleAudiences = [clientId, "other-client"];
        string[] singleAudience = [clientId];
        var validPayload = new JwtPayload
        {
            [JwtRegisteredClaimNames.Aud] = multipleAudiences,
            ["azp"] = clientId,
        };
        var missingAuthorizedParty = new JwtPayload
        {
            [JwtRegisteredClaimNames.Aud] = multipleAudiences,
        };
        var wrongAuthorizedParty = new JwtPayload
        {
            [JwtRegisteredClaimNames.Aud] = singleAudience,
            ["azp"] = "other-client",
        };

        Assert.True(GoogleIdentityTokenVerifier.HasValidAuthorizedParty(
            new JwtSecurityToken(new JwtHeader(), validPayload),
            clientId));
        Assert.False(GoogleIdentityTokenVerifier.HasValidAuthorizedParty(
            new JwtSecurityToken(new JwtHeader(), missingAuthorizedParty),
            clientId));
        Assert.False(GoogleIdentityTokenVerifier.HasValidAuthorizedParty(
            new JwtSecurityToken(new JwtHeader(), wrongAuthorizedParty),
            clientId));
    }

    [Fact]
    public async Task SessionValidationPreservesLifecycleFailureContractAsync()
    {
        User baseline = CreateUser();
        User?[] unavailableUsers =
        [
            null,
            baseline with { IsActive = false },
            baseline with { IsApproved = false },
            baseline with { ErasedAt = FixedNow },
        ];

        foreach (User? unavailable in unavailableUsers)
        {
            SessionService service = CreateSessionService(unavailable, CreateSession());
            SessionValidationResult result = await service.ValidateAsync(
                7,
                CreateSession().Jti,
                FixedNow.AddMinutes(-1),
                CancellationToken.None);

            Assert.False(result.IsValid);
            Assert.Equal("INVALID_SESSION", result.FailureCode);
            Assert.Equal("Session invalid or expired", result.FailureMessage);
        }
    }

    [Fact]
    public async Task SessionValidationDistinguishesAdministrativeAndDeviceRevocationAsync()
    {
        User globallyRevoked = CreateUser() with { SessionValidAfter = FixedNow };
        UserSession session = CreateSession();
        SessionValidationResult administrative = await CreateSessionService(globallyRevoked, session)
            .ValidateAsync(7, session.Jti, FixedNow.AddSeconds(-1), CancellationToken.None);

        Assert.Equal("SESSION_REVOKED", administrative.FailureCode);
        Assert.Equal("Your session was ended by an administrator", administrative.FailureMessage);

        UserSession?[] unavailableSessions =
        [
            null,
            session with { UserId = 8 },
            session with { RevokedAt = FixedNow.AddSeconds(-1) },
            session with { ExpiresAt = FixedNow },
        ];
        foreach (UserSession? unavailable in unavailableSessions)
        {
            SessionValidationResult device = await CreateSessionService(CreateUser(), unavailable)
                .ValidateAsync(7, session.Jti, FixedNow.AddSeconds(-1), CancellationToken.None);

            Assert.Equal("SESSION_REVOKED", device.FailureCode);
            Assert.Equal("This session was signed out", device.FailureMessage);
        }
    }

    [Fact]
    public async Task SessionValidationReturnsCurrentUserWhenBothLocksPassAsync()
    {
        UserSession session = CreateSession();
        SessionValidationResult result = await CreateSessionService(CreateUser(), session)
            .ValidateAsync(7, session.Jti, FixedNow.AddMinutes(-1), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(7, result.User?.Id);
        Assert.Null(result.FailureCode);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task AdministrativeSessionListingMasksDeviceEvidenceWithoutManagePermissionAsync()
    {
        User viewer = CreateUser() with
        {
            RolePermissionKeys = new HashSet<string>([PermissionKeys.UsersRead], StringComparer.Ordinal),
        };
        UserSession session = CreateSession() with
        {
            UserId = 8,
            UserAgent = "sensitive-device",
            IpAddress = "203.0.113.42",
        };
        User target = CreateUser() with { Id = 8, Email = "target@example.com" };
        SessionService service = CreateSessionService(
            new StubUserRepository(viewer, target),
            session);

        IReadOnlyList<SessionResponse> result = await service.ListAsync(
            viewer.Id,
            targetUserId: 8,
            includeDeviceDetails: true,
            currentJti: null,
            CancellationToken.None);

        SessionResponse listed = Assert.Single(result);
        Assert.Null(listed.UserAgent);
        Assert.Null(listed.Ip);
    }

    [Fact]
    public async Task AdministrativeSessionRevocationEnforcesHierarchyAndAuditsAllowedChangesAsync()
    {
        User delegateUser = CreateUser() with
        {
            RolePermissionKeys = new HashSet<string>([PermissionKeys.PresenceKick], StringComparer.Ordinal),
        };
        User administrator = CreateUser() with
        {
            Id = 8,
            Email = "administrator@example.com",
            RoleName = SystemRoleNames.Administrator,
            RolePermissionKeys = new HashSet<string>([PermissionKeys.PresenceKick], StringComparer.Ordinal),
        };
        SessionService deniedService = CreateSessionService(
            new StubUserRepository(delegateUser, administrator),
            CreateSession() with { UserId = administrator.Id });

        ApiException denied = await Assert.ThrowsAsync<ApiException>(() => deniedService.RevokeAsync(
            delegateUser.Id,
            administrator.Id,
            CreateSession().Id,
            CancellationToken.None));

        Assert.Equal("INSUFFICIENT_PRIVILEGE", denied.Code);

        User adminActor = delegateUser with
        {
            RoleName = SystemRoleNames.Administrator,
            RolePermissionKeys = new HashSet<string>([PermissionKeys.PresenceKick], StringComparer.Ordinal),
        };
        User target = administrator with
        {
            RoleName = SystemRoleNames.DefaultUser,
            RolePermissionKeys = new HashSet<string>(StringComparer.Ordinal),
        };
        var events = new CapturingSecurityEventRepository();
        SessionService allowedService = CreateSessionService(
            new StubUserRepository(adminActor, target),
            CreateSession() with { UserId = target.Id },
            events);

        await allowedService.RevokeAsync(
            adminActor.Id,
            target.Id,
            CreateSession().Id,
            CancellationToken.None);

        SecurityEvent securityEvent = Assert.Single(events.Added);
        Assert.Equal(SecurityEventSeverity.Warning, securityEvent.Severity);
        Assert.Equal("user", securityEvent.TargetType);
        Assert.Equal(target.Id.ToString(CultureInfo.InvariantCulture), securityEvent.TargetId);
        Assert.Equal("single", securityEvent.Metadata["scope"]);
    }

    private static SharpbillOptions CreateOptions() => new()
    {
        AppEnvironment = "local",
        Session = new SessionOptions
        {
            ActiveSecret = "identity-test-secret-0123456789abcdef0123456789abcdef",
            Issuer = "sharpbill",
            Audience = "sharpbill-web",
            LifetimeHours = 8,
            MaxActiveSessionsPerUser = 20,
        },
        Retention = new RetentionOptions
        {
            LegalAcceptanceDays = 2555,
            SecurityEventDays = 400,
            PreciseLocationHours = 24,
            NonceBatchSize = 500,
        },
        IdentityProviders = new IdentityProviderOptions
        {
            VerificationMaxConcurrency = 8,
            NetworkMaxConcurrency = 2,
        },
    };

    private static SessionService CreateSessionService(User? user, UserSession? session) =>
        CreateSessionService(new StubUserRepository(user), session);

    private static SessionService CreateSessionService(
        IUserRepository users,
        UserSession? session,
        CapturingSecurityEventRepository? events = null) => new(
        new StubSessionRepository(session),
        users,
        events ?? new CapturingSecurityEventRepository(),
        new StubLegalService(),
        new TrackingUnitOfWork(),
        new FixedClock(FixedNow),
        new Sharpbill.Infrastructure.Runtime.RequestContextAccessor(),
        new SessionJwtIssuer(Options.Create(CreateOptions())),
        Options.Create(CreateOptions()));

    private static User CreateUser() => new()
    {
        Id = 7,
        Email = "session@example.com",
        DisplayName = "Session User",
        RoleId = 2,
        RoleName = "user",
        IsActive = true,
        IsApproved = true,
        AccessVersion = 1,
        LastSeenAt = FixedNow,
        CreatedAt = FixedNow.AddDays(-1),
        UpdatedAt = FixedNow,
    };

    private static UserSession CreateSession() => new()
    {
        Id = 11,
        UserId = 7,
        Jti = Guid.Parse("fc1a3e27-8c04-4f19-a011-41331536a310"),
        CreatedAt = FixedNow.AddMinutes(-1),
        LastSeenAt = FixedNow,
        ExpiresAt = FixedNow.AddHours(1),
    };

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class TrackingUnitOfWork : IUnitOfWork
    {
        public int Commits { get; private set; }
        public int Rollbacks { get; private set; }

        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken)
        {
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

    private sealed class InMemoryNonceRepository : INonceRepository
    {
        private readonly Dictionary<string, LoginNonce> _nonces = new(StringComparer.Ordinal);

        public Task<int> CountActiveAsync(DateTime now, CancellationToken cancellationToken) =>
            Task.FromResult(_nonces.Values.Count(nonce => nonce.ExpiresAt > now));

        public Task AddAsync(LoginNonce nonce, CancellationToken cancellationToken)
        {
            _nonces.Add(nonce.Nonce, nonce);
            return Task.CompletedTask;
        }

        public Task<bool> ConsumeAsync(
            string nonce,
            DateTime now,
            CancellationToken cancellationToken)
        {
            bool consumed = _nonces.TryGetValue(nonce, out LoginNonce? value) &&
                value.ExpiresAt > now && _nonces.Remove(nonce);
            return Task.FromResult(consumed);
        }

        public Task<int> PruneExpiredAsync(
            DateTime now,
            int limit,
            CancellationToken cancellationToken)
        {
            string[] expired = _nonces.Values
                .Where(nonce => nonce.ExpiresAt <= now)
                .OrderBy(static nonce => nonce.ExpiresAt)
                .ThenBy(static nonce => nonce.Nonce, StringComparer.Ordinal)
                .Take(limit)
                .Select(static nonce => nonce.Nonce)
                .ToArray();
            foreach (string nonce in expired)
            {
                _nonces.Remove(nonce);
            }

            return Task.FromResult(expired.Length);
        }
    }

    private sealed class StubSettingsRepository : ISettingsRepository
    {
        private static readonly SiteSettings Settings = new()
        {
            DefaultRoleId = 2,
            UpdatedAt = FixedNow,
        };

        public Task<SiteSettings?> GetAsync(bool forUpdate, CancellationToken cancellationToken) =>
            Task.FromResult<SiteSettings?>(Settings);

        public Task<SiteSettings?> GetForShareAsync(CancellationToken cancellationToken) =>
            Task.FromResult<SiteSettings?>(Settings);

        public Task UpdateAsync(SiteSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubLegalService : ILegalService
    {
        public LegalManifestResponse GetManifest() => throw new NotSupportedException();

        public void RequireCurrentAcceptance(bool accepted, string bundleVersion) =>
            throw new NotSupportedException();

        public Task RecordAcceptanceAsync(
            int userId,
            RequestContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubSessionRepository(UserSession? session) : ISessionRepository
    {
        public Task<UserSession?> FindByJtiAsync(
            Guid jti,
            bool forUpdate,
            CancellationToken cancellationToken) => Task.FromResult(session);

        public Task<UserSession?> FindAsync(
            int sessionId,
            bool forUpdate,
            CancellationToken cancellationToken) => Task.FromResult(session);

        public Task<IReadOnlyList<UserSession>> ListActiveAsync(
            int userId,
            DateTime now,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UserSession>>(session is null ? [] : [session]);

        public Task<int> CountActiveAsync(
            int userId,
            DateTime now,
            CancellationToken cancellationToken) => Task.FromResult(session is null ? 0 : 1);

        public Task<int> AddAsync(UserSession newSession, CancellationToken cancellationToken) =>
            Task.FromResult(1);

        public Task TouchAsync(
            int sessionId,
            DateTime seenAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RevokeAsync(
            int sessionId,
            DateTime revokedAt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> RevokeAllAsync(
            int userId,
            DateTime revokedAt,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> PruneAsync(
            DateTime cutoff,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class StubUserRepository : IUserRepository
    {
        private readonly IReadOnlyDictionary<int, User> _users;

        public StubUserRepository(User? user)
            : this(user is null ? [] : [user])
        {
        }

        public StubUserRepository(params User[] users)
        {
            _users = users.ToDictionary(static user => user.Id);
        }

        public Task<User?> FindAsync(
            int userId,
            bool forUpdate,
            CancellationToken cancellationToken) =>
            Task.FromResult(_users.GetValueOrDefault(userId));

        public Task<User?> FindForAuthenticationAsync(
            int userId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_users.GetValueOrDefault(userId));

        public Task<User?> FindByEmailAsync(
            string email,
            bool forUpdate,
            CancellationToken cancellationToken) =>
            Task.FromResult(_users.Values.SingleOrDefault(
                user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<User?> FindByEmailForAuthenticationAsync(
            string email,
            CancellationToken cancellationToken) =>
            Task.FromResult(_users.Values.SingleOrDefault(
                user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<(IReadOnlyList<User> Items, int Total)> ListAsync(
            UserQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<(IReadOnlyList<User>, int)>(([], 0));

        public Task<IReadOnlyList<User>> ListForExportAsync(
            UserQuery query,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<User>>([]);

        public Task<int> CountActiveAdministratorsAsync(
            bool forUpdate,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> AddAsync(User newUser, CancellationToken cancellationToken) => Task.FromResult(1);

        public Task UpdateAsync(User updatedUser, CancellationToken cancellationToken) => Task.CompletedTask;

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
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<User>>([]);
    }

    private sealed class CapturingLegalRepository : ILegalAcceptanceRepository
    {
        public List<LegalAcceptance> Added { get; } = [];

        public Task<long> AddAsync(LegalAcceptance acceptance, CancellationToken cancellationToken)
        {
            Added.Add(acceptance);
            return Task.FromResult(1L);
        }

        public Task<IReadOnlyList<LegalAcceptance>> ListForUserAsync(
            int userId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LegalAcceptance>>([]);

        public Task<int> ErasePersonalDataAsync(
            int userId,
            DateTime erasedAt,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<int> PruneAsync(
            DateTime cutoff,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class CapturingSecurityEventRepository : ISecurityEventRepository
    {
        public List<SecurityEvent> Added { get; } = [];

        public Task<long> AddWithPendingDeliveryAsync(
            SecurityEvent securityEvent,
            CancellationToken cancellationToken)
        {
            Added.Add(securityEvent);
            return Task.FromResult(1L);
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
}
