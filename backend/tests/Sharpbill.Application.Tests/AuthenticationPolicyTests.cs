using Sharpbill.Application.Common;
using Sharpbill.Application.Identity;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;

namespace Sharpbill.Application.Tests;

public sealed class AuthenticationPolicyTests
{
    [Fact]
    public void ProviderEnablementRequiresSiteAndPublicClientConfiguration()
    {
        var policy = new AuthenticationPolicy(Options());
        var googleOnly = Settings(allowGoogle: true, allowMicrosoft: false);

        Assert.True(policy.ProviderEnabled(googleOnly, ProviderContract.Google));
        Assert.False(policy.ProviderEnabled(googleOnly, ProviderContract.Microsoft));
        Assert.False(policy.ProviderEnabled(null, ProviderContract.Google));
        Assert.Equal("google-client", policy.ClientId(ProviderContract.Google));
        Assert.Equal("microsoft-client", policy.ClientId(ProviderContract.Microsoft));
        Assert.True(policy.DevelopmentAuthenticationEnabled());
    }

    [Fact]
    public void GoogleBootstrapHonorsSubjectAndLocalDevelopmentEmail()
    {
        var localPolicy = new AuthenticationPolicy(Options());
        var productionPolicy = new AuthenticationPolicy(Options() with { IsLocal = false });

        Assert.True(localPolicy.IsAdministratorBootstrap(Identity(
            ProviderContract.Google,
            subject: "google-admin")));
        Assert.True(localPolicy.IsAdministratorBootstrap(Identity(
            ProviderContract.Google,
            email: "developer@example.test")));
        Assert.False(productionPolicy.IsAdministratorBootstrap(Identity(
            ProviderContract.Google,
            email: "developer@example.test")));
    }

    [Fact]
    public void MicrosoftBootstrapRequiresMatchingTenantAndObject()
    {
        var policy = new AuthenticationPolicy(Options());

        Assert.True(policy.IsAdministratorBootstrap(Identity(
            ProviderContract.Microsoft,
            subject: "microsoft-admin",
            tenantId: "TENANT-1")));
        Assert.False(policy.IsAdministratorBootstrap(Identity(
            ProviderContract.Microsoft,
            subject: "other-object",
            tenantId: "tenant-1")));
        Assert.False(policy.IsAdministratorBootstrap(Identity(
            ProviderContract.Microsoft,
            subject: "microsoft-admin",
            tenantId: "other-tenant")));
    }

    [Fact]
    public void MicrosoftIdentityRequiresTenantNamespace()
    {
        ApiException exception = Assert.Throws<ApiException>(() =>
            AuthenticationPolicy.IdentityNamespace(Identity(
                ProviderContract.Microsoft,
                tenantId: null)));

        Assert.Equal(401, exception.StatusCode);
        Assert.Equal("INVALID_IDENTITY", exception.Code);
        Assert.Equal(
            "tenant-1",
            AuthenticationPolicy.IdentityNamespace(Identity(
                ProviderContract.Microsoft,
                tenantId: "tenant-1")));
        Assert.Equal(
            string.Empty,
            AuthenticationPolicy.IdentityNamespace(Identity(ProviderContract.Google)));
    }

    [Fact]
    public void AuthenticatablePolicyPreservesAccountStateFailures()
    {
        User baseline = User();

        Assert.Equal(
            "ACCOUNT_ERASED",
            Assert.Throws<ApiException>(() =>
                AuthenticationPolicy.RequireAuthenticatable(
                    baseline with { ErasedAt = DateTime.UtcNow })).Code);
        Assert.Equal(
            "PENDING_APPROVAL",
            Assert.Throws<ApiException>(() =>
                AuthenticationPolicy.RequireAuthenticatable(
                    baseline with { IsApproved = false })).Code);
        Assert.Equal(
            "ACCOUNT_DISABLED",
            Assert.Throws<ApiException>(() =>
                AuthenticationPolicy.RequireAuthenticatable(
                    baseline with { IsActive = false })).Code);
        Assert.True(AuthenticationPolicy.IsAuthenticatable(baseline));
    }

    [Fact]
    public void ProviderMappingsRemainStable()
    {
        Assert.Equal("google", AuthenticationPolicy.ProviderName(ProviderContract.Google));
        Assert.Equal("Microsoft", AuthenticationPolicy.ProviderDisplayName(ProviderContract.Microsoft));
        Assert.Equal(
            IdentityProvider.Dev,
            AuthenticationPolicy.ToDomainProvider(ProviderContract.Dev));
        Assert.Equal(
            "PROVIDER_DISABLED",
            AuthenticationPolicy.ProviderDisabled(ProviderContract.Google).Code);
    }

    private static AuthenticationPolicyOptions Options() => new()
    {
        IsLocal = true,
        GoogleClientId = "google-client",
        MicrosoftClientId = "microsoft-client",
        DevelopmentAuthenticationEnabled = true,
        GoogleAdminSubjects = new HashSet<string>(["google-admin"], StringComparer.Ordinal),
        MicrosoftAdminTenantId = "tenant-1",
        MicrosoftAdminObjectIds = new HashSet<string>(
            ["microsoft-admin"],
            StringComparer.OrdinalIgnoreCase),
        DevelopmentAdminEmails = new HashSet<string>(
            ["developer@example.test"],
            StringComparer.OrdinalIgnoreCase),
    };

    private static SiteSettings Settings(bool allowGoogle, bool allowMicrosoft) => new()
    {
        DefaultRoleId = 2,
        AllowGoogle = allowGoogle,
        AllowMicrosoft = allowMicrosoft,
    };

    private static VerifiedIdentity Identity(
        ProviderContract provider,
        string subject = "subject",
        string email = "user@example.test",
        string? tenantId = "tenant-1") => new()
        {
            Provider = provider,
            Subject = subject,
            Email = email,
            TenantId = tenantId,
        };

    private static User User() => new()
    {
        Id = 7,
        Email = "user@example.test",
        RoleId = 2,
        RoleName = "user",
        IsActive = true,
        IsApproved = true,
    };
}
