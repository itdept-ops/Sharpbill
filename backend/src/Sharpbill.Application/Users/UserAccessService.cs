using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Application.Users;

public sealed class UserAccessService : IUserAccessService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITransactionExecutor _transactions;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IPermissionRepository _permissions;
    private readonly UserOperationContext _context;
    private readonly UserAuditWriter _audit;
    private readonly IClock _clock;

    public UserAccessService(
        IUnitOfWork unitOfWork,
        ITransactionExecutor transactions,
        IUserRepository users,
        IRoleRepository roles,
        IPermissionRepository permissions,
        UserOperationContext context,
        UserAuditWriter audit,
        IClock clock)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<UserResponse> AssignRoleAsync(
        int userId,
        int actorUserId,
        RoleAssignRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRoleAssignment(request);
        if (userId == actorUserId)
        {
            throw ApiException.BadRequest(
                "CANNOT_MODIFY_SELF",
                "You cannot change your own role");
        }

        return _transactions.ExecuteTransactionAsync(
            _unitOfWork,
            nameof(AssignRoleAsync),
            async _ =>
            {
                await _context.RequireSettingsAsync(true, cancellationToken).ConfigureAwait(false);
                Role role = await _roles.FindAsync(request.RoleId, true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ApiException.BadRequest("UNKNOWN_ROLE", "No such role");
                (User actor, User target) = await _context.LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.UsersManage);
                RbacHierarchyPolicy.EnsureRoleAssignable(actor, role);
                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                RbacHierarchyPolicy.RequireVersion(
                    request.ExpectedVersion,
                    target.AccessVersion,
                    "User access");
                if (RbacHierarchyPolicy.IsAdministrator(target) &&
                    !string.Equals(role.Name, SystemRoleNames.Administrator, StringComparison.Ordinal) &&
                    await _users.CountActiveAdministratorsAsync(true, cancellationToken)
                        .ConfigureAwait(false) <= 1)
                {
                    throw ApiException.Forbidden(
                        "LAST_ADMIN",
                        "Cannot demote the last remaining admin");
                }

                User updated = target with
                {
                    RoleId = role.Id,
                    RoleName = role.Name,
                    RolePermissionKeys = role.PermissionKeys,
                    AccessVersion = target.AccessVersion + 1,
                    UpdatedAt = _clock.UtcNow,
                };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                await _context.EnsureAdministrationAvailableAsync(cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(
                    "user.role.changed",
                    actor.Id,
                    "user",
                    target.Id,
                    new Dictionary<string, object?>
                    {
                        ["before"] = new Dictionary<string, object?>
                        {
                            ["role_id"] = target.RoleId,
                            ["role_name"] = target.RoleName,
                            ["access_version"] = target.AccessVersion,
                        },
                        ["after"] = new Dictionary<string, object?>
                        {
                            ["role_id"] = updated.RoleId,
                            ["role_name"] = updated.RoleName,
                            ["access_version"] = updated.AccessVersion,
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
                return UserResponseMapper.ToResponse(updated, actor, _clock.UtcNow);
            },
            cancellationToken);
    }

    public Task<UserResponse> SetPermissionsAsync(
        int userId,
        int actorUserId,
        PermissionGrantRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePermissionGrant(request);
        if (userId == actorUserId)
        {
            throw ApiException.BadRequest(
                "CANNOT_MODIFY_SELF",
                "You cannot change your own permissions");
        }

        string[] requestedKeys = RbacHierarchyPolicy.NormalizePermissionKeys(request.PermissionKeys)
            .ToArray();
        return _transactions.ExecuteTransactionAsync(
            _unitOfWork,
            nameof(SetPermissionsAsync),
            async _ =>
            {
                IReadOnlyList<Permission> permissions = requestedKeys.Length == 0
                    ? []
                    : await _permissions.FindByKeysAsync(requestedKeys, cancellationToken)
                        .ConfigureAwait(false);
                string[] unknown = requestedKeys
                    .Except(permissions.Select(static permission => permission.Key), StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (unknown.Length > 0)
                {
                    throw ApiException.BadRequest(
                        "UNKNOWN_PERMISSION",
                        $"Unknown permission(s): {string.Join(", ", unknown)}");
                }

                (User actor, User target) = await _context.LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.UsersManage);
                RbacHierarchyPolicy.EnsurePermissionsGrantable(actor, requestedKeys);
                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                RbacHierarchyPolicy.RequireVersion(
                    request.ExpectedVersion,
                    target.AccessVersion,
                    "User access");

                User updated = target with
                {
                    DirectPermissionKeys = requestedKeys.ToHashSet(StringComparer.Ordinal),
                    AccessVersion = target.AccessVersion + 1,
                    UpdatedAt = _clock.UtcNow,
                };
                await _users.ReplaceDirectPermissionsAsync(
                    target.Id,
                    permissions.Select(static permission => permission.Id).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(
                    "user.permissions.changed",
                    actor.Id,
                    "user",
                    target.Id,
                    new Dictionary<string, object?>
                    {
                        ["before"] = new Dictionary<string, object?>
                        {
                            ["permissions"] = SecurityEventMetadata.SummarizeStrings(
                                target.DirectPermissionKeys),
                            ["access_version"] = target.AccessVersion,
                        },
                        ["after"] = new Dictionary<string, object?>
                        {
                            ["permissions"] = SecurityEventMetadata.SummarizeStrings(requestedKeys),
                            ["access_version"] = updated.AccessVersion,
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
                return UserResponseMapper.ToResponse(updated, actor, _clock.UtcNow);
            },
            cancellationToken);
    }

    private static void ValidateRoleAssignment(RoleAssignRequest request)
    {
        var errors = new List<ValidationFailure>();
        if (request.RoleId < 1)
        {
            errors.Add(new("role_id", "OUT_OF_RANGE", "role_id must be positive"));
        }

        if (request.ExpectedVersion is < 1)
        {
            errors.Add(new(
                "expected_version",
                "OUT_OF_RANGE",
                "expected_version must be positive"));
        }

        (errors.Count == 0 ? ValidationResult.Success : ValidationResult.Failure(errors))
            .ThrowIfInvalid();
    }

    private static void ValidatePermissionGrant(PermissionGrantRequest request)
    {
        var errors = new List<ValidationFailure>();
        if (request.PermissionKeys.Count > DomainLimits.MaxPermissionKeysPerMutation)
        {
            errors.Add(new(
                "permission_keys",
                "INVALID_COUNT",
                "permission_keys cannot contain more than 100 values"));
        }

        if (request.PermissionKeys.Any(static key =>
            string.IsNullOrWhiteSpace(key) || key.Length > 100))
        {
            errors.Add(new(
                "permission_keys",
                "INVALID_PERMISSION_KEY",
                "permission keys must be non-empty and no longer than 100 characters"));
        }

        if (request.ExpectedVersion is < 1)
        {
            errors.Add(new(
                "expected_version",
                "OUT_OF_RANGE",
                "expected_version must be positive"));
        }

        (errors.Count == 0 ? ValidationResult.Success : ValidationResult.Failure(errors))
            .ThrowIfInvalid();
    }
}
