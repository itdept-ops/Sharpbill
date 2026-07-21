using Sharpbill.Application.Validation;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Services.Business;

namespace Sharpbill.IntegrationTests.Business;

public sealed class BusinessServiceFixture
{
    public BusinessServiceFixture()
    {
        Settings.Value = BusinessTestData.Settings();
        var usersRead = BusinessTestData.Permission(1, PermissionKeys.UsersRead);
        var usersManage = BusinessTestData.Permission(2, PermissionKeys.UsersManage);
        var usersExport = BusinessTestData.Permission(3, PermissionKeys.UsersExport);
        var rolesManage = BusinessTestData.Permission(4, PermissionKeys.RolesManage);
        var presenceView = BusinessTestData.Permission(5, PermissionKeys.PresenceView);
        var presenceKick = BusinessTestData.Permission(6, PermissionKeys.PresenceKick);
        var settingsManage = BusinessTestData.Permission(7, PermissionKeys.SettingsManage);
        var privacyManage = BusinessTestData.Permission(8, PermissionKeys.PrivacyManage);
        Permission[] permissions =
        [
            usersRead,
            usersManage,
            usersExport,
            rolesManage,
            presenceView,
            presenceKick,
            settingsManage,
            privacyManage,
        ];
        foreach (Permission permission in permissions)
        {
            Permissions.Items[permission.Id] = permission;
        }

        Roles.Items[1] = BusinessTestData.Role(1, SystemRoleNames.Administrator, permissions);
        Roles.Items[2] = BusinessTestData.Role(
            2,
            SystemRoleNames.DefaultUser,
            usersRead,
            presenceView);
    }

    public FakeUnitOfWork UnitOfWork { get; } = new();
    public FakeUserRepository Users { get; } = new();
    public FakeRoleRepository Roles { get; } = new();
    public FakePermissionRepository Permissions { get; } = new();
    public FakeSettingsRepository Settings { get; } = new();
    public FakeHealthRepository Health { get; } = new();
    public FakeSessionService Sessions { get; } = new();
    public FakeSecurityEventService SecurityEvents { get; } = new();
    public FakeGeoService Geo { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeRequestContextAccessor RequestContext { get; } = new();
    public SharpbillOptions Options { get; } = BusinessTestData.Options();

    public UserService CreateUserService() => new(
        UnitOfWork,
        Users,
        Roles,
        Permissions,
        Settings,
        Health,
        Sessions,
        SecurityEvents,
        Geo,
        Clock,
        RequestContext,
        BusinessTestData.WrappedOptions(Options),
        new UserQueryValidator(),
        new ProfileUpdateRequestValidator(),
        new BulkActionRequestValidator(),
        new LocationUpdateRequestValidator());

    public RoleService CreateRoleService() => new(
        UnitOfWork,
        Users,
        Roles,
        Permissions,
        Settings,
        SecurityEvents,
        Clock,
        RequestContext,
        new RoleCreateRequestValidator(),
        new RoleUpdateRequestValidator());

    public PermissionService CreatePermissionService() => new(
        UnitOfWork,
        Users,
        Permissions,
        SecurityEvents,
        Clock,
        RequestContext,
        new PermissionCreateRequestValidator());

    public SettingsService CreateSettingsService() => new(
        UnitOfWork,
        Users,
        Roles,
        Settings,
        Health,
        SecurityEvents,
        Clock,
        RequestContext,
        BusinessTestData.WrappedOptions(Options));

    public PrivacyService CreatePrivacyService() => new(
        UnitOfWork,
        Users,
        Settings,
        SecurityEvents,
        Clock,
        RequestContext,
        new RetentionHoldUpdateRequestValidator(),
        BusinessTestData.WrappedOptions(Options));
}
