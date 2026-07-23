using Microsoft.Extensions.Options;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed class AuthenticationPolicy
{
    private readonly SharpbillOptions _options;

    public AuthenticationPolicy(IOptions<SharpbillOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public bool IsAdministratorBootstrap(VerifiedIdentity identity) => identity.Provider switch
    {
        ProviderContract.Google =>
            _options.IdentityProviders.GoogleAdminSubjects.Contains(identity.Subject) ||
            (_options.IsLocal &&
             _options.IdentityProviders.DevelopmentAdminEmails.Contains(identity.Email)),
        ProviderContract.Microsoft =>
            !string.IsNullOrWhiteSpace(_options.IdentityProviders.MicrosoftAdminTenantId) &&
            string.Equals(
                identity.TenantId,
                _options.IdentityProviders.MicrosoftAdminTenantId,
                StringComparison.OrdinalIgnoreCase) &&
            _options.IdentityProviders.MicrosoftAdminObjectIds.Contains(identity.Subject),
        _ => false,
    };

    public bool ProviderEnabled(SiteSettings? settings, ProviderContract provider) =>
        settings is not null && provider switch
        {
            ProviderContract.Google => settings.AllowGoogle &&
                !string.IsNullOrWhiteSpace(_options.IdentityProviders.GoogleClientId),
            ProviderContract.Microsoft => settings.AllowMicrosoft &&
                !string.IsNullOrWhiteSpace(_options.IdentityProviders.MicrosoftClientId),
            _ => false,
        };

    public bool DevelopmentAuthenticationEnabled() =>
        DevelopmentAuthenticationGuard.IsEnabled(_options);

    public static string IdentityNamespace(VerifiedIdentity identity)
    {
        if (identity.Provider != ProviderContract.Microsoft)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(identity.TenantId))
        {
            throw ApiException.Unauthorized(
                "INVALID_IDENTITY",
                "Microsoft identity is missing its tenant");
        }

        return identity.TenantId;
    }

    public static void RequireAuthenticatable(User user)
    {
        if (user.ErasedAt is not null)
        {
            throw ApiException.Forbidden("ACCOUNT_ERASED", "This account has been erased");
        }

        if (!user.IsApproved)
        {
            throw ApiException.Forbidden(
                "PENDING_APPROVAL",
                "Your account is awaiting administrator approval");
        }

        if (!user.IsActive)
        {
            throw ApiException.Forbidden("ACCOUNT_DISABLED", "This account has been deactivated");
        }
    }

    public static bool IsAuthenticatable(User user) =>
        user.ErasedAt is null && user.IsActive && user.IsApproved;

    public static ApiException ProviderDisabled(ProviderContract provider) =>
        ApiException.Forbidden(
            "PROVIDER_DISABLED",
            $"{ProviderDisplayName(provider)} sign-in is currently disabled");

    public static string ProviderName(ProviderContract provider) => provider switch
    {
        ProviderContract.Google => "google",
        ProviderContract.Microsoft => "microsoft",
        _ => "dev",
    };

    public static string ProviderDisplayName(ProviderContract provider) => provider switch
    {
        ProviderContract.Google => "Google",
        ProviderContract.Microsoft => "Microsoft",
        _ => "Development",
    };

    public static IdentityProvider ToDomainProvider(ProviderContract provider) => provider switch
    {
        ProviderContract.Google => IdentityProvider.Google,
        ProviderContract.Microsoft => IdentityProvider.Microsoft,
        _ => IdentityProvider.Dev,
    };
}
