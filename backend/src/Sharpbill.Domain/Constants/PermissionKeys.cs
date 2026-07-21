namespace Sharpbill.Domain.Constants;

public static class PermissionKeys
{
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string UsersExport = "users.export";
    public const string RolesManage = "roles.manage";
    public const string PresenceView = "presence.view";
    public const string PresenceKick = "presence.kick";
    public const string SettingsManage = "settings.manage";
    public const string LogsView = "logs.view";
    public const string SecurityEventsView = "security_events.view";
    public const string PrivacyManage = "privacy.manage";

    public static IReadOnlySet<string> BuiltIn { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        UsersRead,
        UsersManage,
        UsersExport,
        RolesManage,
        PresenceView,
        PresenceKick,
        SettingsManage,
        LogsView,
        SecurityEventsView,
        PrivacyManage,
    };
}

public static class SystemRoleNames
{
    public const string Administrator = "admin";
    public const string DefaultUser = "user";
}

public static class DomainLimits
{
    public const int MaxPermissionKeysPerMutation = 100;
    public const int MaxBulkUsers = 500;
    public const int MaxPresenceRoster = 500;
    public const int MaxExportRows = 10_000;
    public const int MaxSecurityEventMetadataBytes = 4_096;
    public const int MaxSecurityEventMetadataDepth = 4;
    public const int MaxSecurityEventListItems = 50;
}
