using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.Application.Users;

public static class UserResponseMapper
{
    private const int OnlineWindowSeconds = 90;

    public static UserResponse ToResponse(
        User user,
        User viewer,
        DateTime now,
        bool? includeLocation = null,
        bool? includeIdentitySubjects = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(viewer);

        bool showLocation = includeLocation ??
            (user.Id == viewer.Id ||
             viewer.EffectivePermissionKeys.Contains(PermissionKeys.UsersManage));
        bool showIdentitySubjects = includeIdentitySubjects ??
            (user.Id == viewer.Id || RbacHierarchyPolicy.IsAdministrator(viewer));
        IReadOnlyList<ProviderContract> providers = user.Identities
            .Select(static identity => ToProviderContract(identity.Provider))
            .Distinct()
            .ToArray();

        return new UserResponse
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Title = user.Title,
            Department = user.Department,
            Phone = user.Phone,
            Location = showLocation ? user.Location : null,
            Timezone = showLocation ? user.Timezone : null,
            Bio = user.Bio,
            AccentColor = user.AccentColor,
            UiPreferences = ToUiPreferencesContract(user.UiPreferences),
            Role = user.RoleName,
            RoleId = user.RoleId,
            Permissions = user.EffectivePermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            RolePermissions = user.RolePermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            DirectPermissions = user.DirectPermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            AccessVersion = user.AccessVersion,
            IsActive = user.IsActive,
            IsApproved = user.IsApproved,
            Status = ToUserStatusContract(user.Status),
            Identities = showIdentitySubjects
                ? user.Identities.Select(static identity => new IdentityResponse
                {
                    Provider = ToProviderContract(identity.Provider),
                    Namespace = string.IsNullOrEmpty(identity.ProviderNamespace)
                        ? null
                        : identity.ProviderNamespace,
                    Subject = identity.ProviderSubject,
                }).ToArray()
                : [],
            AuthProviders = providers,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            LastSeenAt = user.LastSeenAt,
            Online = user.LastSeenAt is { } lastSeen &&
                lastSeen >= now.AddSeconds(-OnlineWindowSeconds),
            LastLatitude = showLocation ? user.LastLatitude : null,
            LastLongitude = showLocation ? user.LastLongitude : null,
            LastLocationAccuracy = showLocation ? user.LastLocationAccuracy : null,
            LastLocationAt = showLocation ? user.LastLocationAt : null,
        };
    }

    private static ProviderContract ToProviderContract(IdentityProvider provider) => provider switch
    {
        IdentityProvider.Google => ProviderContract.Google,
        IdentityProvider.Microsoft => ProviderContract.Microsoft,
        IdentityProvider.Dev => ProviderContract.Dev,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider"),
    };

    private static UserStatusContract ToUserStatusContract(UserStatus status) => status switch
    {
        UserStatus.Active => UserStatusContract.Active,
        UserStatus.Pending => UserStatusContract.Pending,
        UserStatus.Disabled => UserStatusContract.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown user status"),
    };

    private static UiPreferencesContract? ToUiPreferencesContract(UiPreferences? preferences)
    {
        if (preferences is null)
        {
            return null;
        }

        var contract = new UiPreferencesContract();
        if (preferences.BaseTone is not null)
        {
            contract = contract with { BaseTone = preferences.BaseTone };
        }

        if (preferences.BackgroundDepth is not null)
        {
            contract = contract with { BackgroundDepth = preferences.BackgroundDepth };
        }

        if (preferences.BorderGlow is not null)
        {
            contract = contract with { BorderGlow = preferences.BorderGlow };
        }

        if (preferences.GlowIntensity is not null)
        {
            contract = contract with { GlowIntensity = preferences.GlowIntensity };
        }

        if (preferences.Scanlines is not null)
        {
            contract = contract with { Scanlines = preferences.Scanlines };
        }

        if (preferences.CornerRadius is not null)
        {
            contract = contract with { CornerRadius = preferences.CornerRadius };
        }

        if (preferences.Motion is not null)
        {
            contract = contract with { Motion = preferences.Motion };
        }

        if (preferences.RainDensity is not null)
        {
            contract = contract with { RainDensity = preferences.RainDensity };
        }

        if (preferences.RainSpeed is not null)
        {
            contract = contract with { RainSpeed = preferences.RainSpeed };
        }

        if (preferences.RainGlyphs is not null)
        {
            contract = contract with { RainGlyphs = preferences.RainGlyphs };
        }

        if (preferences.FontFamily is not null)
        {
            contract = contract with { FontFamily = preferences.FontFamily };
        }

        if (preferences.TextScale is not null)
        {
            contract = contract with { TextScale = preferences.TextScale };
        }

        if (preferences.Density is not null)
        {
            contract = contract with { Density = preferences.Density };
        }

        if (preferences.HighContrastText is not null)
        {
            contract = contract with { HighContrastText = preferences.HighContrastText };
        }

        if (preferences.ReduceTransparency is not null)
        {
            contract = contract with { ReduceTransparency = preferences.ReduceTransparency };
        }

        if (preferences.FocusRing is not null)
        {
            contract = contract with { FocusRing = preferences.FocusRing };
        }

        if (preferences.ZebraRows is not null)
        {
            contract = contract with { ZebraRows = preferences.ZebraRows };
        }

        if (preferences.LinkUnderlines is not null)
        {
            contract = contract with { LinkUnderlines = preferences.LinkUnderlines };
        }

        if (preferences.Version is not null)
        {
            contract = contract with { Version = preferences.Version };
        }

        return contract;
    }
}
