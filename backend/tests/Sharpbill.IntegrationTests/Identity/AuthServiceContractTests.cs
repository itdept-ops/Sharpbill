using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Services.Identity;

namespace Sharpbill.IntegrationTests.Identity;

public sealed class AuthServiceContractTests
{
    [Fact]
    public async Task ConfigurationRequiresBothProviderConfigurationAndSiteEnablementAsync()
    {
        var fixture = new AuthServiceTestFixture();
        fixture.Settings.Value = AuthServiceTestFixture.CreateSettings(
            allowGoogle: true,
            allowMicrosoft: false,
            calmMode: true);

        AuthConfigResponse configured = await fixture.CreateService()
            .GetConfigurationAsync(CancellationToken.None);

        Assert.True(configured.Google);
        Assert.Equal("google-client", configured.GoogleClientId);
        Assert.False(configured.Microsoft);
        Assert.Null(configured.MicrosoftClientId);
        Assert.False(configured.Dev);
        Assert.True(configured.Calm);

        fixture.Configuration.IdentityProviders.GoogleClientId = null;
        AuthConfigResponse missingClient = await fixture.CreateService()
            .GetConfigurationAsync(CancellationToken.None);

        Assert.False(missingClient.Google);
        Assert.Null(missingClient.GoogleClientId);
    }

    [Fact]
    public async Task DisabledProviderIsRejectedAndAuditedBeforeTokenVerificationAsync()
    {
        var fixture = new AuthServiceTestFixture();
        fixture.Settings.Value = AuthServiceTestFixture.CreateSettings(allowGoogle: false);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateService().LoginAsync(
                ProviderContract.Google,
                LoginRequest("not-a-token"),
                fixture.RequestContext,
                CancellationToken.None));

        Assert.Equal(403, exception.StatusCode);
        Assert.Equal("PROVIDER_DISABLED", exception.Code);
        Assert.Equal(0, fixture.Verifier.Calls);
        SecurityEvent securityEvent = Assert.Single(fixture.SecurityEvents.Added);
        Assert.Equal("auth.login", securityEvent.EventType);
        Assert.Equal(SecurityEventOutcome.Denied, securityEvent.Outcome);
        Assert.Equal("identity_provider", securityEvent.TargetType);
        Assert.Equal("google", securityEvent.TargetId);
        Assert.Equal("PROVIDER_DISABLED", securityEvent.Metadata["reason"]);
    }

    [Theory]
    [InlineData(false, 401, "INVALID_TOKEN", SecurityEventOutcome.Denied)]
    [InlineData(true, 503, "PROVIDER_UNAVAILABLE", SecurityEventOutcome.Failure)]
    public async Task ProviderVerificationFailuresMapAndAuditAsync(
        bool unavailable,
        int expectedStatus,
        string expectedCode,
        SecurityEventOutcome expectedOutcome)
    {
        const string nonce = "auth-contract-nonce";
        var fixture = new AuthServiceTestFixture();
        fixture.Verifier.Failure = unavailable
            ? new IdentityProviderUnavailableException("provider unavailable")
            : new IdentityTokenException("invalid token");

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateService().LoginAsync(
                ProviderContract.Google,
                LoginRequest(CreateUnsignedToken(nonce)),
                fixture.RequestContext,
                CancellationToken.None));

        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(1, fixture.Verifier.Calls);
        Assert.Equal(nonce, fixture.Verifier.ExpectedNonce);
        SecurityEvent securityEvent = Assert.Single(fixture.SecurityEvents.Added);
        Assert.Equal(expectedOutcome, securityEvent.Outcome);
        Assert.Equal(expectedCode, securityEvent.Metadata["reason"]);
        Assert.Equal("google", securityEvent.Metadata["provider"]);
    }

    [Fact]
    public async Task ExistingIdentityLoginRefreshesEvidenceAndCommitsAuditSessionAndLegalEvidenceAsync()
    {
        const string nonce = "existing-user-nonce";
        var fixture = new AuthServiceTestFixture();
        UserIdentity identity = new()
        {
            Id = 19,
            UserId = 7,
            Provider = IdentityProvider.Google,
            ProviderSubject = fixture.Verifier.Result!.Subject,
            CreatedAt = AuthServiceTestFixture.Now.AddDays(-30),
            UpdatedAt = AuthServiceTestFixture.Now.AddDays(-1),
        };
        User user = AuthServiceTestFixture.CreateUser() with
        {
            Identities = [identity],
        };
        fixture.Users.Items[user.Id] = user;
        fixture.Identities.Existing = identity;

        AuthenticatedSession result = await fixture.CreateService().LoginAsync(
            ProviderContract.Google,
            LoginRequest(CreateUnsignedToken(nonce)),
            fixture.RequestContext,
            CancellationToken.None);

        Assert.Equal(user.Id, result.User.Id);
        Assert.Equal(user.Id, result.Session.UserId);
        Assert.Equal(nonce, fixture.Verifier.ExpectedNonce);
        UserIdentity evidence = Assert.Single(fixture.Identities.EvidenceUpdates);
        Assert.Equal("example.test", evidence.ProviderHostedDomain);
        Assert.Equal(AuthServiceTestFixture.Now, evidence.UpdatedAt);
        User updated = fixture.Users.Items[user.Id];
        Assert.Equal(AuthServiceTestFixture.Now, updated.LastLoginAt);
        Assert.Equal(AuthServiceTestFixture.Now, updated.LastSeenAt);
        Assert.Equal(AuthServiceTestFixture.Now, updated.UpdatedAt);
        Assert.Single(fixture.Sessions.Added);
        Assert.Equal([user.Id], fixture.Legal.RecordedUserIds);
        Assert.Equal(2, fixture.Legal.RequireCalls);
        SecurityEvent securityEvent = Assert.Single(fixture.SecurityEvents.Added);
        Assert.Equal(SecurityEventOutcome.Success, securityEvent.Outcome);
        Assert.Equal("google", securityEvent.Metadata["provider"]);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
        Assert.Equal(0, fixture.UnitOfWork.Rollbacks);
    }

    [Fact]
    public async Task LogoutDoesNotRevokeASessionOwnedByAnotherContextUserAsync()
    {
        var fixture = new AuthServiceTestFixture();
        UserSession session = CreateSession(userId: 7);
        fixture.Sessions.Items[session.Jti] = session;

        await fixture.CreateService().LogoutAsync(
            fixture.RequestContext with
            {
                SessionJti = session.Jti,
                SessionUserId = 8,
            },
            CancellationToken.None);

        Assert.Empty(fixture.Sessions.RevokedSessionIds);
        Assert.Null(fixture.Sessions.Items[session.Jti].RevokedAt);
        SecurityEvent securityEvent = Assert.Single(fixture.SecurityEvents.Added);
        Assert.Equal(8, securityEvent.ActorUserId);
        Assert.Equal(false, securityEvent.Metadata["session_revoked"]);
    }

    [Fact]
    public async Task LogoutIsIdempotentAfterTheSessionHasBeenRevokedAsync()
    {
        var fixture = new AuthServiceTestFixture();
        UserSession session = CreateSession(userId: 7);
        fixture.Sessions.Items[session.Jti] = session;
        RequestContext context = fixture.RequestContext with
        {
            SessionJti = session.Jti,
            SessionUserId = session.UserId,
        };
        var service = fixture.CreateService();

        await service.LogoutAsync(context, CancellationToken.None);
        await service.LogoutAsync(context, CancellationToken.None);

        Assert.Equal([session.Id], fixture.Sessions.RevokedSessionIds);
        Assert.Equal(AuthServiceTestFixture.Now, fixture.Sessions.Items[session.Jti].RevokedAt);
        Assert.Equal(2, fixture.SecurityEvents.Added.Count);
        Assert.Equal(true, fixture.SecurityEvents.Added[0].Metadata["session_revoked"]);
        Assert.Equal(false, fixture.SecurityEvents.Added[1].Metadata["session_revoked"]);
        Assert.Equal(2, fixture.UnitOfWork.Commits);
    }

    [Fact]
    public async Task CurrentUserRejectsEveryUnavailableAccountStateAsync()
    {
        User baseline = AuthServiceTestFixture.CreateUser();
        User[] unavailableUsers =
        [
            baseline with { IsActive = false },
            baseline with { IsApproved = false },
            baseline with { ErasedAt = AuthServiceTestFixture.Now },
        ];

        foreach (User unavailable in unavailableUsers)
        {
            var fixture = new AuthServiceTestFixture();
            fixture.Users.Items[unavailable.Id] = unavailable;

            ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
                fixture.CreateService().GetCurrentUserAsync(
                    unavailable.Id,
                    CancellationToken.None));

            Assert.Equal(401, exception.StatusCode);
            Assert.Equal("INVALID_SESSION", exception.Code);
        }
    }

    private static TokenLoginRequest LoginRequest(string idToken) => new()
    {
        IdToken = idToken,
        LegalAccepted = true,
        LegalBundleVersion = "auth-contract-legal",
    };

    private static string CreateUnsignedToken(string nonce)
    {
        var token = new JwtSecurityToken(claims: [new Claim("nonce", nonce)]);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static UserSession CreateSession(int userId) => new()
    {
        Id = 41,
        UserId = userId,
        Jti = Guid.Parse("25636433-8229-4b90-a2e4-ec804604f6ae"),
        CreatedAt = AuthServiceTestFixture.Now.AddMinutes(-10),
        ExpiresAt = AuthServiceTestFixture.Now.AddHours(1),
    };
}
