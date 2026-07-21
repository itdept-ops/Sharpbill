using Sharpbill.Application.Common;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Application.Policies;

public static class RbacHierarchyPolicy
{
    private static readonly IReadOnlySet<string> BaselinePermissions = new HashSet<string>(
        [PermissionKeys.UsersRead, PermissionKeys.PresenceView],
        StringComparer.Ordinal);

    public static bool IsAdministrator(User user) =>
        string.Equals(user.RoleName, SystemRoleNames.Administrator, StringComparison.Ordinal);

    public static void RequirePermission(User actor, string permission)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        if (!actor.EffectivePermissionKeys.Contains(permission))
        {
            throw ApiException.Forbidden("FORBIDDEN", $"Missing permission: {permission}");
        }
    }

    public static void EnsureCanManageTarget(User actor, User target)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(target);
        if (target.ErasedAt is not null)
        {
            throw ApiException.Conflict("ACCOUNT_ERASED", "An erased account cannot be modified");
        }

        var unheldSensitivePermissions = target.EffectivePermissionKeys
            .Except(actor.EffectivePermissionKeys, StringComparer.Ordinal)
            .Except(BaselinePermissions, StringComparer.Ordinal);
        var targetOutranksActor =
            IsAdministrator(target) || unheldSensitivePermissions.Any();
        if (!IsAdministrator(actor) && targetOutranksActor)
        {
            throw ApiException.Forbidden(
                "INSUFFICIENT_PRIVILEGE",
                "You cannot modify or revoke access for a principal who outranks you");
        }
    }

    public static void EnsureRoleAssignable(User actor, Role role)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(role);
        RequireAccessAssignmentAuthority(actor);

        if (string.Equals(role.Name, SystemRoleNames.Administrator, StringComparison.Ordinal) &&
            !IsAdministrator(actor))
        {
            throw ApiException.Forbidden(
                "INSUFFICIENT_PRIVILEGE",
                "Only an admin can assign the admin role");
        }

        if (!IsAdministrator(actor) &&
            !role.PermissionKeys.IsSubsetOf(actor.EffectivePermissionKeys))
        {
            throw ApiException.Forbidden(
                "INSUFFICIENT_PRIVILEGE",
                "You cannot assign a role with permissions you do not hold");
        }
    }

    public static void EnsurePermissionsGrantable(User actor, IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(permissions);
        RequireAccessAssignmentAuthority(actor);
        if (IsAdministrator(actor))
        {
            return;
        }

        var missing = NormalizePermissionKeys(permissions)
            .Except(actor.EffectivePermissionKeys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw ApiException.Forbidden(
                "INSUFFICIENT_PRIVILEGE",
                $"You can only grant permissions you hold; missing: {string.Join(", ", missing)}");
        }
    }

    public static IReadOnlyList<string> NormalizePermissionKeys(IEnumerable<string> permissions) =>
        permissions
            .Select(static key => key.Trim().ToLowerInvariant())
            .Where(static key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static void RequireVersion(int? expected, int current, string resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        if (expected is null)
        {
            throw ApiException.PreconditionRequired(
                "PRECONDITION_REQUIRED",
                $"{resource} updates require the version returned by the latest read");
        }

        if (expected.Value != current)
        {
            throw ApiException.Conflict(
                "STALE_WRITE",
                $"{resource} changed since it was loaded; refresh and retry");
        }
    }

    private static void RequireAccessAssignmentAuthority(User actor)
    {
        if (!actor.EffectivePermissionKeys.Contains(PermissionKeys.RolesManage))
        {
            throw ApiException.Forbidden(
                "INSUFFICIENT_PRIVILEGE",
                "Changing access requires both users.manage and roles.manage");
        }
    }
}
