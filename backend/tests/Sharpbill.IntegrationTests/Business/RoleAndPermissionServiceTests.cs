using Sharpbill.Application.Common;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.IntegrationTests.Business;

public sealed class RoleAndPermissionServiceTests
{
    [Fact]
    public async Task UpdateRequiresTheLatestRoleVersionAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Roles.Items[3] = BusinessTestData.Role(
            3,
            "auditor",
            fixture.Permissions.Items[PermissionId(PermissionKeys.UsersRead)]) with
        {
            IsSystem = false,
            Version = 4,
        };

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateRoleService().UpdateAsync(
                3,
                1,
                new RoleUpdateRequest
                {
                    Description = "changed",
                    ExpectedVersion = 3,
                },
                CancellationToken.None));

        Assert.Equal("STALE_WRITE", exception.Code);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
        Assert.Empty(fixture.SecurityEvents.Writes);
    }

    [Fact]
    public async Task CreatePreventsDelegatesFromGrantingPermissionsTheyDoNotHoldAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            "role-manager",
            [PermissionKeys.RolesManage, PermissionKeys.UsersRead]);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateRoleService().CreateAsync(
                1,
                new RoleCreateRequest
                {
                    Name = "operators",
                    PermissionKeys = [PermissionKeys.SettingsManage],
                },
                CancellationToken.None));

        Assert.Equal("INSUFFICIENT_PRIVILEGE", exception.Code);
        Assert.DoesNotContain(
            fixture.Roles.Items.Values,
            static role => string.Equals(role.Name, "operators", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteProtectsDefaultAndOptimisticallyVersionsCustomRolesAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Roles.Items[3] = BusinessTestData.Role(3, "custom") with
        {
            IsSystem = false,
            Version = 2,
        };
        fixture.Settings.Value = fixture.Settings.Value! with { DefaultRoleId = 3 };

        ApiException inUse = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateRoleService().DeleteAsync(3, 1, 2, CancellationToken.None));

        Assert.Equal("ROLE_IN_USE", inUse.Code);

        fixture.Settings.Value = fixture.Settings.Value with { DefaultRoleId = 2 };
        ApiException stale = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateRoleService().DeleteAsync(3, 1, 1, CancellationToken.None));

        Assert.Equal("STALE_WRITE", stale.Code);
        Assert.True(fixture.Roles.Items.ContainsKey(3));
    }

    [Fact]
    public async Task CreatePermissionNormalizesTheKeyAndRecordsEvidenceAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);

        PermissionResponse response = await fixture.CreatePermissionService().CreateAsync(
            1,
            new PermissionCreateRequest
            {
                Key = "  Reports.Export  ",
                Description = "Export reports",
            },
            CancellationToken.None);

        Assert.Equal("reports.export", response.Key);
        Assert.Equal("rbac.permission.created", Assert.Single(fixture.SecurityEvents.Writes).EventType);
        Assert.Equal("business-test", fixture.SecurityEvents.Writes[0].RequestId);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
    }

    private static User Administrator(int id) => BusinessTestData.User(
        id,
        SystemRoleNames.Administrator,
        PermissionKeys.BuiltIn);

    private static int PermissionId(string key) => key switch
    {
        PermissionKeys.UsersRead => 1,
        PermissionKeys.UsersManage => 2,
        PermissionKeys.UsersExport => 3,
        PermissionKeys.RolesManage => 4,
        PermissionKeys.PresenceView => 5,
        PermissionKeys.PresenceKick => 6,
        PermissionKeys.SettingsManage => 7,
        PermissionKeys.PrivacyManage => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown test permission"),
    };
}
