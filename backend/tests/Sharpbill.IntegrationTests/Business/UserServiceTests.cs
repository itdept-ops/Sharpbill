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
    public async Task ListAndGetEnforceUsersReadWhileAllowingSelfReadAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            SystemRoleNames.DefaultUser,
            []) with
        {
            Location = "Portland",
            Timezone = "America/Los_Angeles",
        };
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead]);

        ApiException listException = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateUserService().ListAsync(
                new UserQuery(),
                1,
                CancellationToken.None));
        ApiException getException = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateUserService().GetAsync(
                2,
                1,
                CancellationToken.None));
        UserResponse self = await fixture.CreateUserService().GetAsync(
            1,
            1,
            CancellationToken.None);

        Assert.Equal("FORBIDDEN", listException.Code);
        Assert.Equal("FORBIDDEN", getException.Code);
        Assert.Equal("Portland", self.Location);
        Assert.Equal("America/Los_Angeles", self.Timezone);
    }

    [Fact]
    public async Task ListAndGetApplyLocationVisibilityAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            "reader",
            [PermissionKeys.UsersRead]);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead]) with
        {
            Location = "Portland",
            Timezone = "America/Los_Angeles",
            LastLatitude = 45.52,
            LastLongitude = -122.68,
            LastLocationAt = fixture.Clock.UtcNow,
        };
        var service = fixture.CreateUserService();

        UserListResponse readerList = await service.ListAsync(
            new UserQuery(),
            1,
            CancellationToken.None);
        UserResponse readerGet = await service.GetAsync(2, 1, CancellationToken.None);

        UserResponse hiddenListUser = Assert.Single(
            readerList.Items,
            static user => user.Id == 2);
        Assert.Null(hiddenListUser.Location);
        Assert.Null(hiddenListUser.Timezone);
        Assert.Null(hiddenListUser.LastLatitude);
        Assert.Null(readerGet.Location);
        Assert.Null(readerGet.Timezone);
        Assert.Null(readerGet.LastLatitude);

        fixture.Users.Items[1] = BusinessTestData.User(
            1,
            "manager",
            [PermissionKeys.UsersRead, PermissionKeys.UsersManage]);

        UserListResponse managerList = await service.ListAsync(
            new UserQuery(),
            1,
            CancellationToken.None);
        UserResponse managerGet = await service.GetAsync(2, 1, CancellationToken.None);

        UserResponse visibleListUser = Assert.Single(
            managerList.Items,
            static user => user.Id == 2);
        Assert.Equal("Portland", visibleListUser.Location);
        Assert.Equal("America/Los_Angeles", visibleListUser.Timezone);
        Assert.Equal(45.52, visibleListUser.LastLatitude);
        Assert.Equal("Portland", managerGet.Location);
        Assert.Equal("America/Los_Angeles", managerGet.Timezone);
        Assert.Equal(45.52, managerGet.LastLatitude);
    }

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
    public async Task AssignRoleIncrementsAccessVersionAndAuditsAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead, PermissionKeys.PresenceView]);

        UserResponse response = await fixture.CreateUserService().AssignRoleAsync(
            2,
            1,
            new RoleAssignRequest { RoleId = 1, ExpectedVersion = 1 },
            CancellationToken.None);

        Assert.Equal(SystemRoleNames.Administrator, response.Role);
        Assert.Equal(1, response.RoleId);
        Assert.Equal(2, response.AccessVersion);
        Assert.Equal(SystemRoleNames.Administrator, fixture.Users.Items[2].RoleName);
        Assert.Equal(2, fixture.Users.Items[2].AccessVersion);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
        Assert.Equal(0, fixture.UnitOfWork.Rollbacks);
        var securityEvent = Assert.Single(fixture.SecurityEvents.Writes);
        Assert.Equal("user.role.changed", securityEvent.EventType);
        Assert.Equal(1, securityEvent.ActorUserId);
        Assert.Equal("user", securityEvent.TargetType);
        Assert.Equal("2", securityEvent.TargetId);
        Assert.Equal("business-test", securityEvent.RequestId);
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
    public async Task SetPermissionsIncrementsAccessVersionAndAuditsAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead, PermissionKeys.PresenceView]);

        UserResponse response = await fixture.CreateUserService().SetPermissionsAsync(
            2,
            1,
            new PermissionGrantRequest
            {
                PermissionKeys = [PermissionKeys.SettingsManage],
                ExpectedVersion = 1,
            },
            CancellationToken.None);

        Assert.Equal(2, response.AccessVersion);
        Assert.Equal([PermissionKeys.SettingsManage], response.DirectPermissions);
        Assert.Equal(2, fixture.Users.Items[2].AccessVersion);
        Assert.Equal(
            [PermissionKeys.SettingsManage],
            fixture.Users.Items[2].DirectPermissionKeys);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
        Assert.Equal(0, fixture.UnitOfWork.Rollbacks);
        var securityEvent = Assert.Single(fixture.SecurityEvents.Writes);
        Assert.Equal("user.permissions.changed", securityEvent.EventType);
        Assert.Equal(1, securityEvent.ActorUserId);
        Assert.Equal("user", securityEvent.TargetType);
        Assert.Equal("2", securityEvent.TargetId);
        Assert.Equal("business-test", securityEvent.RequestId);
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
    public async Task SetStatusRefusesToDeactivateTheLastAdministratorAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.Items[2] = Administrator(2);
        fixture.Users.ActiveAdministratorCount = 1;

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateUserService().SetStatusAsync(
                2,
                1,
                new StatusUpdateRequest { IsActive = false },
                CancellationToken.None));

        Assert.Equal("LAST_ADMIN", exception.Code);
        Assert.Equal(0, fixture.UnitOfWork.Commits);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
        Assert.Empty(fixture.Users.Updates);
        Assert.Empty(fixture.Sessions.RevokedUserIds);
        Assert.Empty(fixture.SecurityEvents.Writes);
    }

    [Fact]
    public async Task SetStatusDeactivatesUserRevokesSessionsAndAuditsAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead, PermissionKeys.PresenceView]);

        UserResponse response = await fixture.CreateUserService().SetStatusAsync(
            2,
            1,
            new StatusUpdateRequest { IsActive = false },
            CancellationToken.None);

        User updated = fixture.Users.Items[2];
        Assert.False(response.IsActive);
        Assert.False(updated.IsActive);
        Assert.Equal(fixture.Clock.UtcNow, updated.DeactivatedAt);
        Assert.Equal(fixture.Clock.UtcNow, updated.SessionValidAfter);
        Assert.Equal([2], fixture.Sessions.RevokedUserIds);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
        Assert.Equal(0, fixture.UnitOfWork.Rollbacks);
        var securityEvent = Assert.Single(fixture.SecurityEvents.Writes);
        Assert.Equal("user.status.changed", securityEvent.EventType);
        Assert.Equal(1, securityEvent.ActorUserId);
        Assert.Equal("2", securityEvent.TargetId);
    }

    [Fact]
    public async Task ApproveUpdatesUserAndAuditsAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead, PermissionKeys.PresenceView]) with
        {
            IsApproved = false,
        };

        UserResponse response = await fixture.CreateUserService().ApproveAsync(
            2,
            1,
            CancellationToken.None);

        Assert.True(response.IsApproved);
        Assert.True(fixture.Users.Items[2].IsApproved);
        Assert.Equal(fixture.Clock.UtcNow, fixture.Users.Items[2].UpdatedAt);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
        Assert.Equal(0, fixture.UnitOfWork.Rollbacks);
        var securityEvent = Assert.Single(fixture.SecurityEvents.Writes);
        Assert.Equal("user.approved", securityEvent.EventType);
        Assert.Equal(1, securityEvent.ActorUserId);
        Assert.Equal("2", securityEvent.TargetId);
    }

    [Fact]
    public async Task KickRejectsSelfBeforeStartingTransactionAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateUserService().KickAsync(
                1,
                1,
                CancellationToken.None));

        Assert.Equal("CANNOT_MODIFY_SELF", exception.Code);
        Assert.Equal(0, fixture.UnitOfWork.Begins);
        Assert.Equal(0, fixture.UnitOfWork.Commits);
        Assert.Equal(0, fixture.UnitOfWork.Rollbacks);
        Assert.Empty(fixture.Users.Updates);
        Assert.Empty(fixture.Sessions.RevokedUserIds);
        Assert.Empty(fixture.SecurityEvents.Writes);
    }

    [Fact]
    public async Task KickRevokesSessionsUpdatesCutoffAndAuditsAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.Items[2] = BusinessTestData.User(
            2,
            SystemRoleNames.DefaultUser,
            [PermissionKeys.UsersRead, PermissionKeys.PresenceView]);

        UserResponse response = await fixture.CreateUserService().KickAsync(
            2,
            1,
            CancellationToken.None);

        User updated = fixture.Users.Items[2];
        Assert.Equal(fixture.Clock.UtcNow, updated.SessionValidAfter);
        Assert.Equal(fixture.Clock.UtcNow, updated.UpdatedAt);
        Assert.Equal(updated.AccessVersion, response.AccessVersion);
        Assert.Equal([2], fixture.Sessions.RevokedUserIds);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
        Assert.Equal(0, fixture.UnitOfWork.Rollbacks);
        var securityEvent = Assert.Single(fixture.SecurityEvents.Writes);
        Assert.Equal("user.sessions.revoked", securityEvent.EventType);
        Assert.Equal("warning", securityEvent.Severity);
        Assert.Equal(1, securityEvent.ActorUserId);
        Assert.Equal("2", securityEvent.TargetId);
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
        await using var destination = new MemoryStream();
        await export.WriteAsync(destination, CancellationToken.None);
        string csv = Encoding.UTF8.GetString(destination.ToArray());

        Assert.Contains("'=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.Contains("'+SUM", csv, StringComparison.Ordinal);
        Assert.Contains("'@A1", csv, StringComparison.Ordinal);
        Assert.Equal("users.csv", export.FileName);
        Assert.Equal("users.exported", Assert.Single(fixture.SecurityEvents.Writes).EventType);
    }

    [Fact]
    public async Task ExportRejectsCsvAboveConfiguredByteLimitBeforeAuditingAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Options.RequestPipeline.ExportMaxBytes = 32;
        fixture.Users.Items[1] = Administrator(1);
        fixture.Users.ExportItems =
        [
            BusinessTestData.User(
                2,
                SystemRoleNames.DefaultUser,
                [PermissionKeys.UsersRead, PermissionKeys.PresenceView]),
        ];

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateUserService().ExportAsync(
                new UserQuery(),
                1,
                CancellationToken.None));

        Assert.Equal(413, exception.StatusCode);
        Assert.Equal("EXPORT_TOO_LARGE", exception.Code);
        Assert.Empty(fixture.SecurityEvents.Writes);
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
