using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;

namespace Sharpbill.Application.Identity;

public sealed class AuthenticationPolicy
{
    private readonly AuthenticationPolicyOptions _options;

    public AuthenticationPolicy(AuthenticationPolicyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsAdministratorBootstrap(VerifiedIdentity identity) => identity.Provider switch
    {
        ProviderContract.Google =>
            _options.GoogleAdminSubjects.Contains(identity.Subject) ||
            (_options.IsLocal && IsDevelopmentAdministratorEmail(identity.Email)),
        ProviderContract.Microsoft =>
            !string.IsNullOrWhiteSpace(_options.MicrosoftAdminTenantId) &&
            string.Equals(
                identity.TenantId,
                _options.MicrosoftAdminTenantId,
                StringComparison.OrdinalIgnoreCase) &&
            _options.MicrosoftAdminObjectIds.Contains(identity.Subject),
        _ => false,
    };

    public bool ProviderEnabled(SiteSettings? settings, ProviderContract provider) =>
        settings is not null && provider switch
        {
            ProviderContract.Google => settings.AllowGoogle &&
                !string.IsNullOrWhiteSpace(ClientId(provider)),
            ProviderContract.Microsoft => settings.AllowMicrosoft &&
                !string.IsNullOrWhiteSpace(ClientId(provider)),
            _ => false,
        };

    public string? ClientId(ProviderContract provider) => provider switch
    {
        ProviderContract.Google => _options.GoogleClientId,
        ProviderContract.Microsoft => _options.MicrosoftClientId,
        _ => null,
    };

    public bool DevelopmentAuthenticationEnabled() =>
        _options.DevelopmentAuthenticationEnabled;

    public bool IsDevelopmentAdministratorEmail(string email) =>
        _options.DevelopmentAdminEmails.Contains(email);

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
