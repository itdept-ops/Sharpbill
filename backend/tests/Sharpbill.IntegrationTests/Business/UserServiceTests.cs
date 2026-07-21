using System.Text;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.IntegrationTests.Business;

public sealed class UserServiceTests
{
    [Fact]
    public async Task AssignRoleRefusesToDemoteTheLastAdministratorAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.Items[2] = Administrator(2);
        fixture.Users.ActiveAdministratorCount = 1;

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateUserService().AssignRoleAsync(
                2,
                1,
                new RoleAssignRequest { RoleId = 2, ExpectedVersion = 1 },
                CancellationToken.None));

        Assert.Equal("LAST_ADMIN", exception.Code);
        Assert.Equal(0, fixture.UnitOfWork.Commits);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
        Assert.Empty(fixture.SecurityEvents.Writes);
    }

    [Fact]
    public async Task SetPermissionsPreventsPrivilegeAmplificationAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            "manager",
            [PermissionKeys.UsersManage, PermissionKeys.RolesManage]);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead, PermissionKeys.PresenceView]);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateUserService().SetPermissionsAsync(
                2,
                1,
                new PermissionGrantRequest
                {
                    PermissionKeys = [PermissionKeys.SettingsManage],
                    ExpectedVersion = 1,
                },
                CancellationToken.None));

        Assert.Equal("INSUFFICIENT_PRIVILEGE", exception.Code);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
        Assert.Empty(fixture.Users.Updates);
    }

    [Fact]
    public async Task UpdateProfileMergesPreferencesAndHonorsExplicitNullAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead, PermissionKeys.PresenceView]) with
        {
            DisplayName = "Before",
            UiPreferences = new UiPreferences { BaseTone = "abyss" },
        };

        UserResponse merged = await fixture.CreateUserService().UpdateProfileAsync(
            1,
            1,
            new ProfileUpdateRequest
            {
                DisplayName = new PatchField<string?>(null),
                UiPreferences = new UiPreferencesContract
                {
                    GlowIntensity = "intense",
                },
            },
            CancellationToken.None);

        Assert.Null(merged.DisplayName);
        Assert.Equal("abyss", merged.UiPreferences?.BaseTone.Value);
        Assert.Equal("intense", merged.UiPreferences?.GlowIntensity.Value);

        UserResponse reset = await fixture.CreateUserService().UpdateProfileAsync(
            1,
            1,
            new ProfileUpdateRequest
            {
                UiPreferences = new PatchField<UiPreferencesContract?>(null),
            },
            CancellationToken.None);

        Assert.Null(reset.UiPreferences);
        Assert.Equal(2, fixture.UnitOfWork.Commits);
    }

    [Fact]
    public async Task BulkCommitsValidItemsAndReportsPerItemFailuresAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            "manager",
            [PermissionKeys.UsersManage, PermissionKeys.RolesManage]);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead, PermissionKeys.PresenceView]) with
        {
            IsApproved = false,
        };

        BulkActionResponse response = await fixture.CreateUserService().BulkAsync(
            1,
            new BulkActionRequest
            {
                Ids = [2, 1],
                Action = BulkUserActionContract.Approve,
            },
            CancellationToken.None);

        Assert.Equal(1, response.Applied);
        Assert.True(response.Results[0].Ok);
        Assert.Equal("CANNOT_MODIFY_SELF", response.Results[1].Error);
        Assert.True(fixture.Users.Items[2].IsApproved);
        Assert.Single(fixture.SecurityEvents.Writes);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
    }

    [Fact]
    public async Task BulkReportsDatabaseFailureAndContinuesWithTheNextItemAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            "manager",
            [PermissionKeys.UsersManage, PermissionKeys.RolesManage]);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead]) with
        {
            IsApproved = false,
        };
        fixture.Users.Items[3] = BusinessTestData.User(
            3,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead]) with
        {
            IsApproved = false,
        };
        fixture.Users.DatabaseFailureUserIds.Add(2);

        BulkActionResponse response = await fixture.CreateUserService().BulkAsync(
            1,
            new BulkActionRequest
            {
                Ids = [2, 3],
                Action = BulkUserActionContract.Approve,
            },
            CancellationToken.None);

        Assert.Equal(1, response.Applied);
        Assert.Equal("DB_ERROR", response.Results[0].Error);
        Assert.True(response.Results[1].Ok);
        Assert.True(fixture.Users.Items[3].IsApproved);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
    }

    [Fact]
    public async Task ExportNeutralizesSpreadsheetFormulasAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.ExportItems =
        [
            BusinessTestData.User(
                2,
                SystemRoleNames.DefaultUser,
                [PermissionKeys.UsersRead, PermissionKeys.PresenceView]) with
            {
                Email = "=HYPERLINK(\"https://invalid\")",
                DisplayName = "+SUM(1,1)",
                Location = "@A1",
            },
        ];

        ExportDocument export = await fixture.CreateUserService().ExportAsync(
            new UserQuery(),
            1,
            CancellationToken.None);
        string csv = Encoding.UTF8.GetString(export.Content.Span);

        Assert.Contains("'=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.Contains("'+SUM", csv, StringComparison.Ordinal);
        Assert.Contains("'@A1", csv, StringComparison.Ordinal);
        Assert.Equal("users.csv", export.FileName);
        Assert.Equal("users.exported", Assert.Single(fixture.SecurityEvents.Writes).EventType);
    }

    [Fact]
    public async Task UpdateLocationStoresCaptureDeadlineAndOfflinePlaceAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead]);

        await fixture.CreateUserService().UpdateLocationAsync(
            1,
            new LocationUpdateRequest
            {
                Latitude = 45.52,
                Longitude = -122.99,
                Accuracy = 12,
            },
            CancellationToken.None);

        User updated = fixture.Users.Items[1];
        Assert.Equal("Hillsboro", updated.Location);
        Assert.Equal("America/Los_Angeles", updated.Timezone);
        Assert.Equal(fixture.Clock.UtcNow.AddHours(24), updated.LocationRetentionUntil);
    }

    private static User Administrator(int id) => BusinessTestData.User(
        id,
        SystemRoleNames.Administrator,
        PermissionKeys.BuiltIn);
}
