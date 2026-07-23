using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Runtime;
using Sharpbill.Infrastructure.Services.Identity;
using Sharpbill.IntegrationTests.Business;

namespace Sharpbill.IntegrationTests.Identity;

public sealed class DevelopmentAuthServiceTests
{
    private const string DevelopmentSecret =
        "integration-dev-auth-secret-9f6a2d7c";

    [Fact]
    public async Task LoginWithValidSecretDelegatesWithCurrentRequestContextAsync()
    {
        var authService = new TrackingAuthService();
        var requestContext = new RequestContext
        {
            RequestId = "server-request-id",
            ClientRequestId = "client-request-id",
            IpAddress = "203.0.113.42",
            UserAgent = "Sharpbill-test/1.0",
        };
        DevelopmentAuthService service = CreateService(
            authService,
            new FakeRoleRepository(),
            requestContext);
        var request = new DevLoginRequest
        {
            Email = "developer@example.test",
            LegalAccepted = true,
            LegalBundleVersion = "integration-legal",
        };

        AuthenticatedSession result = await service.LoginAsync(
            request,
            DevelopmentSecret,
            CancellationToken.None);

        Assert.Same(authService.Result, result);
        Assert.Same(request, authService.LastRequest);
        Assert.Same(requestContext, authService.LastContext);
        Assert.Equal(1, authService.DevelopmentLoginCalls);
    }

    [Fact]
    public async Task LoginRejectsMissingAndIncorrectSecretsBeforeDelegationAsync()
    {
        var authService = new TrackingAuthService();
        DevelopmentAuthService service = CreateService(
            authService,
            new FakeRoleRepository(),
            new RequestContext());
        var request = new DevLoginRequest
        {
            Email = "developer@example.test",
            LegalAccepted = true,
            LegalBundleVersion = "integration-legal",
        };
        string?[] invalidSecrets =
        [
            null,
            string.Empty,
            "incorrect-secret",
            new string('x', DevelopmentSecret.Length),
        ];

        foreach (string? invalidSecret in invalidSecrets)
        {
            ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
                service.LoginAsync(request, invalidSecret, CancellationToken.None));
            Assert.Equal(404, exception.StatusCode);
            Assert.Equal("NOT_FOUND", exception.Code);
        }

        Assert.Equal(0, authService.DevelopmentLoginCalls);
    }

    [Fact]
    public async Task ListRolesReturnsNamesOrderedByRoleIdAsync()
    {
        var roles = new FakeRoleRepository();
        roles.Items[20] = new Role { Id = 20, Name = "reviewer" };
        roles.Items[3] = new Role { Id = 3, Name = "administrator" };
        roles.Items[11] = new Role { Id = 11, Name = "operator" };
        DevelopmentAuthService service = CreateService(
            new TrackingAuthService(),
            roles,
            new RequestContext());

        IReadOnlyList<string> result = await service.ListRolesAsync(
            DevelopmentSecret,
            CancellationToken.None);

        Assert.Equal(["administrator", "operator", "reviewer"], result);
    }

    private static DevelopmentAuthService CreateService(
        TrackingAuthService authService,
        IRoleRepository roleRepository,
        RequestContext context) =>
        new(
            authService,
            roleRepository,
            new RequestContextAccessor { Current = context },
            Options.Create(new SharpbillOptions
            {
                DevelopmentAuthentication = new DevelopmentAuthenticationOptions
                {
                    Enabled = true,
                    Secret = DevelopmentSecret,
                },
            }));

    private sealed class TrackingAuthService : IAuthService
    {
        public TrackingAuthService()
        {
            DateTime issuedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Result = new AuthenticatedSession(
                new UserResponse
                {
                    Id = 42,
                    Email = "developer@example.test",
                    Role = "administrator",
                },
                new SessionToken
                {
                    Value = "integration-session-token",
                    Jti = Guid.Parse("1f6ff9ce-fc31-40c1-89f5-d4fa3a2173a3"),
                    UserId = 42,
                    IssuedAt = issuedAt,
                    ExpiresAt = issuedAt.AddHours(1),
                });
        }

        public AuthenticatedSession Result { get; }

        public int DevelopmentLoginCalls { get; private set; }

        public DevLoginRequest? LastRequest { get; private set; }

        public RequestContext? LastContext { get; private set; }

        public Task<AuthConfigResponse> GetConfigurationAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuthenticatedSession> LoginAsync(
            ProviderContract provider,
            TokenLoginRequest request,
            RequestContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuthenticatedSession> DevLoginAsync(
            DevLoginRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            DevelopmentLoginCalls++;
            LastRequest = request;
            LastContext = context;
            return Task.FromResult(Result);
        }

        public Task LogoutAsync(RequestContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UserResponse> GetCurrentUserAsync(
            int userId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
