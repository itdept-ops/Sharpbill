using Sharpbill.Application.Common;
using Sharpbill.Contracts.Settings;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Application.Policies;

public static class SettingsPolicy
{
    public static void EnsureProviderAvailable(
        bool currentGoogle,
        bool currentMicrosoft,
        SiteSettingsUpdateRequest update,
        bool googleConfigured,
        bool microsoftConfigured)
    {
        ArgumentNullException.ThrowIfNull(update);
        var google = update.AllowGoogle.HasValue
            ? update.AllowGoogle.Value is true
            : currentGoogle;
        var microsoft = update.AllowMicrosoft.HasValue
            ? update.AllowMicrosoft.Value is true
            : currentMicrosoft;
        if (!google && !microsoft)
        {
            throw ApiException.BadRequest(
                "PROVIDER_REQUIRED",
                "At least one identity provider must remain enabled");
        }

        if (google && !googleConfigured)
        {
            throw ApiException.BadRequest(
                "PROVIDER_NOT_CONFIGURED",
                "Google sign-in is not configured");
        }

        if (microsoft && !microsoftConfigured)
        {
            throw ApiException.BadRequest(
                "PROVIDER_NOT_CONFIGURED",
                "Microsoft sign-in is not configured");
        }
    }

    public static void EnsureSafeDefaultRole(string roleName)
    {
        if (string.Equals(roleName, SystemRoleNames.Administrator, StringComparison.Ordinal))
        {
            throw ApiException.BadRequest(
                "UNSAFE_DEFAULT_ROLE",
                "The administrator role cannot be the default signup role");
        }
    }
}
