using Sharpbill.Application.Identity;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.Application.Tests;

public sealed class IdentityUserMapperTests
{
    [Fact]
    public void MapperPreservesIdentityStatusPermissionsAndPreferences()
    {
        User user = User() with
        {
            IsApproved = false,
            RolePermissionKeys = new HashSet<string>(
                ["users.read", "presence.view"],
                StringComparer.Ordinal),
            DirectPermissionKeys = new HashSet<string>(
                ["settings.manage", "logs.view"],
                StringComparer.Ordinal),
            Identities =
            [
                Identity(1, IdentityProvider.Google, string.Empty, "google-subject"),
                Identity(2, IdentityProvider.Microsoft, "tenant-1", "microsoft-subject"),
                Identity(3, IdentityProvider.Google, string.Empty, "second-google-subject"),
            ],
            UiPreferences = new UiPreferences
            {
                BaseTone = "abyss",
                TextScale = "110",
                ReduceTransparency = true,
            },
        };

        var result = IdentityUserMapper.ToResponse(user, online: true);

        Assert.Equal(UserStatusContract.Pending, result.Status);
        Assert.True(result.Online);
        Assert.Equal(
            ["logs.view", "presence.view", "settings.manage", "users.read"],
            result.Permissions);
        Assert.Equal(["presence.view", "users.read"], result.RolePermissions);
        Assert.Equal(["logs.view", "settings.manage"], result.DirectPermissions);
        Assert.Equal(3, result.Identities.Count);
        Assert.Equal(
            [ProviderContract.Google, ProviderContract.Microsoft],
            result.AuthProviders);
        Assert.Null(result.Identities[0].Namespace);
        Assert.Equal("tenant-1", result.Identities[1].Namespace);
        Assert.Equal("abyss", result.UiPreferences?.BaseTone.Value);
        Assert.Equal("110", result.UiPreferences?.TextScale.Value);
        Assert.True(result.UiPreferences?.ReduceTransparency.Value == true);
    }

    [Fact]
    public void MapperCanSuppressIdentitySubjectsWithoutHidingProviders()
    {
        User user = User() with
        {
            Identities =
            [
                Identity(1, IdentityProvider.Google, string.Empty, "google-subject"),
            ],
        };

        var result = IdentityUserMapper.ToResponse(
            user,
            online: false,
            includeIdentitySubjects: false);

        Assert.Empty(result.Identities);
        Assert.Equal([ProviderContract.Google], result.AuthProviders);
        Assert.False(result.Online);
    }

    private static User User() => new()
    {
        Id = 7,
        Email = "user@example.test",
        DisplayName = "User",
        RoleId = 2,
        RoleName = "user",
        IsActive = true,
        IsApproved = true,
        AccessVersion = 3,
    };

    private static UserIdentity Identity(
        int id,
        IdentityProvider provider,
        string providerNamespace,
        string subject) => new()
        {
            Id = id,
            UserId = 7,
            Provider = provider,
            ProviderNamespace = providerNamespace,
            ProviderSubject = subject,
        };
}
