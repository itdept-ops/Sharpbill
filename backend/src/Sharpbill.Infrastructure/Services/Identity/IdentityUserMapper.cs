using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.Infrastructure.Services.Identity;

internal static class IdentityUserMapper
{
    public static UserResponse ToResponse(User user, bool online, bool includeIdentitySubjects = true) =>
        new()
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Title = user.Title,
            Department = user.Department,
            Phone = user.Phone,
            Location = user.Location,
            Timezone = user.Timezone,
            Bio = user.Bio,
            AccentColor = user.AccentColor,
            UiPreferences = ToContract(user.UiPreferences),
            Role = user.RoleName,
            RoleId = user.RoleId,
            Permissions = user.EffectivePermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            RolePermissions = user.RolePermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            DirectPermissions = user.DirectPermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            AccessVersion = user.AccessVersion,
            IsActive = user.IsActive,
            IsApproved = user.IsApproved,
            Status = user.Status switch
            {
                UserStatus.Active => UserStatusContract.Active,
                UserStatus.Pending => UserStatusContract.Pending,
                _ => UserStatusContract.Disabled,
            },
            Identities = includeIdentitySubjects
                ? user.Identities.Select(ToContract).ToArray()
                : [],
            AuthProviders = user.Identities
                .Select(static identity => ToContract(identity.Provider))
                .Distinct()
                .ToArray(),
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            LastSeenAt = user.LastSeenAt,
            Online = online,
            LastLatitude = user.LastLatitude,
            LastLongitude = user.LastLongitude,
            LastLocationAccuracy = user.LastLocationAccuracy,
            LastLocationAt = user.LastLocationAt,
        };

    private static IdentityResponse ToContract(UserIdentity identity) => new()
    {
        Provider = ToContract(identity.Provider),
        Namespace = string.IsNullOrEmpty(identity.ProviderNamespace) ? null : identity.ProviderNamespace,
        Subject = identity.ProviderSubject,
    };

    private static ProviderContract ToContract(IdentityProvider provider) => provider switch
    {
        IdentityProvider.Google => ProviderContract.Google,
        IdentityProvider.Microsoft => ProviderContract.Microsoft,
        _ => ProviderContract.Dev,
    };

    private static UiPreferencesContract? ToContract(UiPreferences? preferences)
    {
        if (preferences is null)
        {
            return null;
        }

        return new UiPreferencesContract
        {
            BaseTone = Field(preferences.BaseTone),
            BackgroundDepth = Field(preferences.BackgroundDepth),
            BorderGlow = Field(preferences.BorderGlow),
            GlowIntensity = Field(preferences.GlowIntensity),
            Scanlines = Field(preferences.Scanlines),
            CornerRadius = Field(preferences.CornerRadius),
            Motion = Field(preferences.Motion),
            RainDensity = Field(preferences.RainDensity),
            RainSpeed = Field(preferences.RainSpeed),
            RainGlyphs = Field(preferences.RainGlyphs),
            FontFamily = Field(preferences.FontFamily),
            TextScale = Field(preferences.TextScale),
            Density = Field(preferences.Density),
            HighContrastText = Field(preferences.HighContrastText),
            ReduceTransparency = Field(preferences.ReduceTransparency),
            FocusRing = Field(preferences.FocusRing),
            ZebraRows = Field(preferences.ZebraRows),
            LinkUnderlines = Field(preferences.LinkUnderlines),
            Version = Field(preferences.Version),
        };
    }

    private static PatchField<T?> Field<T>(T? value) where T : struct =>
        value.HasValue ? new PatchField<T?>(value) : default;

    private static PatchField<string?> Field(string? value) =>
        value is null ? default : new PatchField<string?>(value);
}
