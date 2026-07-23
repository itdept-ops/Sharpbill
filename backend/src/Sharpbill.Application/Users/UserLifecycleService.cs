using System.Data.Common;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Application.Users;

public sealed class UserLifecycleService : IUserLifecycleService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITransactionExecutor _transactions;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly ISessionService _sessions;
    private readonly UserOperationContext _context;
    private readonly UserAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IValidator<BulkActionRequest> _bulkValidator;

    public UserLifecycleService(
        IUnitOfWork unitOfWork,
        ITransactionExecutor transactions,
        IUserRepository users,
        IRoleRepository roles,
        ISessionService sessions,
        UserOperationContext context,
        UserAuditWriter audit,
        IClock clock,
        IValidator<BulkActionRequest> bulkValidator)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _bulkValidator = bulkValidator ?? throw new ArgumentNullException(nameof(bulkValidator));
    }

    public Task<UserResponse> SetStatusAsync(
        int userId,
        int actorUserId,
        StatusUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (userId == actorUserId)
        {
            throw ApiException.BadRequest(
                "CANNOT_MODIFY_SELF",
                "You cannot deactivate yourself");
        }

        return _transactions.ExecuteTransactionAsync(
            _unitOfWork,
            nameof(SetStatusAsync),
            async _ =>
            {
                await _context.RequireSettingsAsync(true, cancellationToken).ConfigureAwait(false);
                (User actor, User target) = await _context.LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.UsersManage);
                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                DateTime now = _clock.UtcNow;
                if (!request.IsActive &&
                    RbacHierarchyPolicy.IsAdministrator(target) &&
                    await _users.CountActiveAdministratorsAsync(true, cancellationToken)
                        .ConfigureAwait(false) <= 1)
                {
                    throw ApiException.Forbidden(
                        "LAST_ADMIN",
                        "Cannot deactivate the last remaining admin");
                }

                User updated = target with
                {
                    IsActive = request.IsActive,
                    DeactivatedAt = request.IsActive
                        ? null
                        : target.IsActive || target.DeactivatedAt is null
                            ? now
                            : target.DeactivatedAt,
                    SessionValidAfter = request.IsActive ? target.SessionValidAfter : now,
                    UpdatedAt = now,
                };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                if (!request.IsActive)
                {
                    await _sessions.RevokeAllAsync(target.Id, now, cancellationToken)
                        .ConfigureAwait(false);
                    await _context.EnsureAdministrationAvailableAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                await _audit.RecordAsync(
                    "user.status.changed",
                    actor.Id,
                    "user",
                    target.Id,
                    new Dictionary<string, object?>
                    {
                        ["before"] = new Dictionary<string, object?>
                        {
                            ["is_active"] = target.IsActive,
                        },
                        ["after"] = new Dictionary<string, object?>
                        {
                            ["is_active"] = updated.IsActive,
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
                return UserResponseMapper.ToResponse(updated, actor, now);
            },
            cancellationToken);
    }

    public Task<UserResponse> ApproveAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteTransactionAsync(
            _unitOfWork,
            nameof(ApproveAsync),
            async _ =>
            {
                (User actor, User target) = await _context.LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.UsersManage);
                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                User updated = target with { IsApproved = true, UpdatedAt = _clock.UtcNow };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                await _audit.RecordAsync(
                    "user.approved",
                    actor.Id,
                    "user",
                    target.Id,
                    new Dictionary<string, object?>
                    {
                        ["before"] = new Dictionary<string, object?>
                        {
                            ["is_approved"] = target.IsApproved,
                        },
                        ["after"] = new Dictionary<string, object?>
                        {
                            ["is_approved"] = true,
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
                return UserResponseMapper.ToResponse(updated, actor, _clock.UtcNow);
            },
            cancellationToken);

    public Task<UserResponse> KickAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        if (userId == actorUserId)
        {
            throw ApiException.BadRequest(
                "CANNOT_MODIFY_SELF",
                "You cannot kick your own session");
        }

        return _transactions.ExecuteTransactionAsync(
            _unitOfWork,
            nameof(KickAsync),
            async _ =>
            {
                (User actor, User target) = await _context.LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.PresenceKick);
                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                DateTime now = _clock.UtcNow;
                User updated = target with { SessionValidAfter = now, UpdatedAt = now };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                await _sessions.RevokeAllAsync(target.Id, now, cancellationToken)
                    .ConfigureAwait(false);
                await _audit.RecordAsync(
                    "user.sessions.revoked",
                    actor.Id,
                    "user",
                    target.Id,
                    new Dictionary<string, object?> { ["scope"] = "all" },
                    cancellationToken,
                    "warning").ConfigureAwait(false);
                return UserResponseMapper.ToResponse(updated, actor, now);
            },
            cancellationToken);
    }

    public async Task<BulkActionResponse> BulkAsync(
        int actorUserId,
        BulkActionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _bulkValidator.Validate(request).ThrowIfInvalid();
        User initialActor = await _context.RequireActorAsync(
            actorUserId,
            PermissionKeys.UsersManage,
            false,
            cancellationToken).ConfigureAwait(false);
        _ = initialActor;

        if (request.Action == BulkUserActionContract.AssignRole)
        {
            await ValidateBulkRoleAsync(actorUserId, request.RoleId!.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        var results = new List<BulkItemResponse>(request.Ids.Count);
        foreach (int userId in request.Ids)
        {
            try
            {
                await ApplyBulkItemAsync(actorUserId, userId, request, cancellationToken)
                    .ConfigureAwait(false);
                results.Add(new BulkItemResponse { Id = userId, Ok = true });
            }
            catch (ApiException exception)
            {
                results.Add(new BulkItemResponse
                {
                    Id = userId,
                    Ok = false,
                    Error = exception.Code,
                });
            }
            catch (DbException)
            {
                results.Add(new BulkItemResponse { Id = userId, Ok = false, Error = "DB_ERROR" });
            }
        }

        return new BulkActionResponse
        {
            Applied = results.Count(static result => result.Ok),
            Results = results,
        };
    }

    private async Task ValidateBulkRoleAsync(
        int actorUserId,
        int roleId,
        CancellationToken cancellationToken)
    {
        await _transactions.ExecuteTransactionAsync(
            _unitOfWork,
            nameof(ValidateBulkRoleAsync),
            async _ =>
            {
                await _context.RequireSettingsAsync(true, cancellationToken).ConfigureAwait(false);
                Role role = await _roles.FindAsync(roleId, true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ApiException.BadRequest("UNKNOWN_ROLE", "No such role");
                User actor = await _context.RequireActorAsync(
                    actorUserId,
                    PermissionKeys.UsersManage,
                    true,
                    cancellationToken).ConfigureAwait(false);
                RbacHierarchyPolicy.EnsureRoleAssignable(actor, role);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private Task ApplyBulkItemAsync(
        int actorUserId,
        int userId,
        BulkActionRequest request,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteTransactionAsync(
            _unitOfWork,
            nameof(ApplyBulkItemAsync),
            async _ =>
            {
                await _context.RequireSettingsAsync(true, cancellationToken).ConfigureAwait(false);
                Role? role = null;
                if (request.Action == BulkUserActionContract.AssignRole)
                {
                    role = await _roles.FindAsync(request.RoleId!.Value, true, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw ApiException.BadRequest("UNKNOWN_ROLE", "No such role");
                }

                if (userId == actorUserId)
                {
                    throw ApiException.BadRequest("CANNOT_MODIFY_SELF", "Cannot act on yourself");
                }

                (User actor, User target) = await _context.LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.UsersManage);
                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                if (role is not null)
                {
                    RbacHierarchyPolicy.EnsureRoleAssignable(actor, role);
                }

                DateTime now = _clock.UtcNow;
                User updated = await ApplyBulkMutationAsync(
                    target,
                    request.Action,
                    role,
                    now,
                    cancellationToken).ConfigureAwait(false);
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                if (request.Action == BulkUserActionContract.Deactivate)
                {
                    await _sessions.RevokeAllAsync(target.Id, now, cancellationToken)
                        .ConfigureAwait(false);
                }

                await _context.EnsureAdministrationAvailableAsync(cancellationToken)
                    .ConfigureAwait(false);
                string action = ToBulkAction(request.Action);
                string eventType = request.Action switch
                {
                    BulkUserActionContract.Activate => "user.status.changed",
                    BulkUserActionContract.Deactivate => "user.status.changed",
                    BulkUserActionContract.Approve => "user.approved",
                    BulkUserActionContract.AssignRole => "user.role.changed",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(request),
                        request.Action,
                        "Unknown action"),
                };
                await _audit.RecordAsync(
                    eventType,
                    actor.Id,
                    "user",
                    target.Id,
                    new Dictionary<string, object?>
                    {
                        ["bulk"] = true,
                        ["action"] = action,
                        ["before"] = BulkSnapshot(target),
                        ["after"] = BulkSnapshot(updated),
                    },
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task<User> ApplyBulkMutationAsync(
        User target,
        BulkUserActionContract action,
        Role? role,
        DateTime now,
        CancellationToken cancellationToken)
    {
        switch (action)
        {
            case BulkUserActionContract.Activate:
                return target with { IsActive = true, DeactivatedAt = null, UpdatedAt = now };
            case BulkUserActionContract.Deactivate:
                if (RbacHierarchyPolicy.IsAdministrator(target) &&
                    await _users.CountActiveAdministratorsAsync(true, cancellationToken)
                        .ConfigureAwait(false) <= 1)
                {
                    throw ApiException.Forbidden(
                        "LAST_ADMIN",
                        "Cannot deactivate the last remaining admin");
                }

                return target with
                {
                    IsActive = false,
                    DeactivatedAt = target.IsActive || target.DeactivatedAt is null
                        ? now
                        : target.DeactivatedAt,
                    SessionValidAfter = now,
                    UpdatedAt = now,
                };
            case BulkUserActionContract.Approve:
                return target with { IsApproved = true, UpdatedAt = now };
            case BulkUserActionContract.AssignRole:
                ArgumentNullException.ThrowIfNull(role);
                if (RbacHierarchyPolicy.IsAdministrator(target) &&
                    !string.Equals(role.Name, SystemRoleNames.Administrator, StringComparison.Ordinal) &&
                    await _users.CountActiveAdministratorsAsync(true, cancellationToken)
                        .ConfigureAwait(false) <= 1)
                {
                    throw ApiException.Forbidden(
                        "LAST_ADMIN",
                        "Cannot demote the last remaining admin");
                }

                return target with
                {
                    RoleId = role.Id,
                    RoleName = role.Name,
                    RolePermissionKeys = role.PermissionKeys,
                    AccessVersion = target.AccessVersion + 1,
                    UpdatedAt = now,
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action");
        }
    }

    private static Dictionary<string, object?> BulkSnapshot(User user) =>
        new()
        {
            ["is_active"] = user.IsActive,
            ["is_approved"] = user.IsApproved,
            ["role_id"] = user.RoleId,
            ["access_version"] = user.AccessVersion,
        };

    private static string ToBulkAction(BulkUserActionContract action) => action switch
    {
        BulkUserActionContract.Activate => "activate",
        BulkUserActionContract.Deactivate => "deactivate",
        BulkUserActionContract.Approve => "approve",
        BulkUserActionContract.AssignRole => "assign_role",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action"),
    };
}
