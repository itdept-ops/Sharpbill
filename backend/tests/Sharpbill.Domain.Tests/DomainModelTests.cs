using System.Text.RegularExpressions;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.Legal;

namespace Sharpbill.Domain.Tests;

public sealed class DomainModelTests
{
    [Fact]
    public void UserCombinesRoleAndDirectPermissions()
    {
        var user = User(
            rolePermissions: new HashSet<string> { PermissionKeys.UsersRead },
            directPermissions: new HashSet<string> { "reports.run" });

        Assert.Equal(
            ["reports.run", PermissionKeys.UsersRead],
            user.EffectivePermissionKeys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true, true, UserStatus.Active)]
    [InlineData(false, true, UserStatus.Disabled)]
    [InlineData(true, false, UserStatus.Pending)]
    [InlineData(false, false, UserStatus.Pending)]
    public void UserStatusFollowsLifecycleFlags(bool active, bool approved, UserStatus expected)
    {
        var user = User(active: active, approved: approved);

        Assert.Equal(expected, user.Status);
    }

    [Fact]
    public void LegalBundleV3IsCompleteAndImmutable()
    {
        Assert.Equal("2026-07-21-v3", LegalBundleV3.BundleVersion);
        Assert.Equal(new DateOnly(2026, 7, 21), LegalBundleV3.EffectiveDate);
        Assert.Equal(4, LegalBundleV3.Documents.Length);
        Assert.Equal(
            ["acceptable_use", "eula", "privacy", "terms"],
            LegalBundleV3.Documents.Select(static document => document.Key).Order());
        Assert.All(
            LegalBundleV3.Documents,
            static document => Assert.Matches("^[0-9a-f]{64}$", document.Sha256));
        Assert.Equal(
            LegalAcceptanceAction.Acknowledgement,
            LegalBundleV3.Documents.Single(static document => document.Key == "privacy").Acceptance);
    }

    [Fact]
    public void BuiltInPermissionCatalogMatchesAuthorizationSurface()
    {
        Assert.Equal(10, PermissionKeys.BuiltIn.Count);
        Assert.Contains(PermissionKeys.PrivacyManage, PermissionKeys.BuiltIn);
        Assert.Contains(PermissionKeys.SecurityEventsView, PermissionKeys.BuiltIn);
        Assert.All(
            PermissionKeys.BuiltIn,
            static permission => Assert.Matches(
                new Regex("^[a-z][a-z0-9_]*\\.[a-z0-9]+$", RegexOptions.CultureInvariant),
                permission));
    }

    private static User User(
        bool active = true,
        bool approved = true,
        IReadOnlySet<string>? rolePermissions = null,
        IReadOnlySet<string>? directPermissions = null) =>
        new()
        {
            Id = 1,
            Email = "user@example.test",
            RoleId = 2,
            RoleName = SystemRoleNames.DefaultUser,
            IsActive = active,
            IsApproved = approved,
            RolePermissionKeys = rolePermissions ?? new HashSet<string>(),
            DirectPermissionKeys = directPermissions ?? new HashSet<string>(),
        };
}
