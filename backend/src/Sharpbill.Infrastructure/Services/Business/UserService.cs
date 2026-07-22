using System.Data.Common;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Services;

namespace Sharpbill.Infrastructure.Services.Business;

public sealed class UserService : IUserService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IPermissionRepository _permissions;
    private readonly ISettingsRepository _settings;
    private readonly IHealthRepository _health;
    private readonly ISessionService _sessions;
    private readonly ISecurityEventService _securityEvents;
    private readonly IGeoService _geo;
    private readonly IClock _clock;
    private readonly IRequestContextAccessor _requestContextAccessor;
    private readonly RetentionOptions _retention;
    private readonly int _exportMaxBytes;
    private readonly IValidator<UserQuery> _queryValidator;
    private readonly IValidator<ProfileUpdateRequest> _profileValidator;
    private readonly IValidator<BulkActionRequest> _bulkValidator;
    private readonly IValidator<LocationUpdateRequest> _locationValidator;

    public UserService(
        IUnitOfWork unitOfWork,
        IUserRepository users,
        IRoleRepository roles,
        IPermissionRepository permissions,
        ISettingsRepository settings,
        IHealthRepository health,
        ISessionService sessions,
        ISecurityEventService securityEvents,
        IGeoService geo,
        IClock clock,
        IRequestContextAccessor requestContextAccessor,
        IOptions<SharpbillOptions> options,
        IValidator<UserQuery> queryValidator,
        IValidator<ProfileUpdateRequest> profileValidator,
        IValidator<BulkActionRequest> bulkValidator,
        IValidator<LocationUpdateRequest> locationValidator)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _securityEvents = securityEvents ?? throw new ArgumentNullException(nameof(securityEvents));
        _geo = geo ?? throw new ArgumentNullException(nameof(geo));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _requestContextAccessor = requestContextAccessor ??
            throw new ArgumentNullException(nameof(requestContextAccessor));
        ArgumentNullException.ThrowIfNull(options);
        _retention = options.Value.Retention;
        _exportMaxBytes = options.Value.RequestPipeline.ExportMaxBytes;
        _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
        _profileValidator = profileValidator ?? throw new ArgumentNullException(nameof(profileValidator));
        _bulkValidator = bulkValidator ?? throw new ArgumentNullException(nameof(bulkValidator));
        _locationValidator = locationValidator ?? throw new ArgumentNullException(nameof(locationValidator));
    }

    public async Task<UserListResponse> ListAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        _queryValidator.Validate(query).ThrowIfInvalid();
        User actor = await RequireActorAsync(
            actorUserId,
            PermissionKeys.UsersRead,
            false,
            cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<User> items, int total) =
            await _users.ListAsync(query, cancellationToken).ConfigureAwait(false);
        DateTime now = _clock.UtcNow;
        return new UserListResponse
        {
            Items = items
                .Select(user => BusinessServiceSupport.ToUserResponse(user, actor, now))
                .ToArray(),
            Total = total,
        };
    }

    public async Task<UserResponse> GetAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        User actor = await RequireActorAsync(actorUserId, null, false, cancellationToken)
            .ConfigureAwait(false);
        if (userId != actorUserId)
        {
            RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.UsersRead);
        }

        User target = await FindUserAsync(userId, false, cancellationToken).ConfigureAwait(false);
        return BusinessServiceSupport.ToUserResponse(target, actor, _clock.UtcNow);
    }

    public Task<UserResponse> UpdateProfileAsync(
        int userId,
        int actorUserId,
        ProfileUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _profileValidator.Validate(request).ThrowIfInvalid();
        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                if (userId != actorUserId &&
                    !actor.EffectivePermissionKeys.Contains(PermissionKeys.UsersManage))
                {
                    throw ApiException.Forbidden(
                        "FORBIDDEN",
                        "You can only edit your own profile");
                }

                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                User updated = ApplyProfilePatch(target, request) with { UpdatedAt = _clock.UtcNow };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                return BusinessServiceSupport.ToUserResponse(
                    updated,
                    actor,
                    _clock.UtcNow,
                    includeLocation: true);
            },
            cancellationToken);
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

        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                await RequireSettingsAsync(true, cancellationToken).ConfigureAwait(false);
                Role role = await _roles.FindAsync(request.RoleId, true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ApiException.BadRequest("UNKNOWN_ROLE", "No such role");
                (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
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
                await EnsureAdministrationAvailableAsync(cancellationToken).ConfigureAwait(false);
                await RecordEventAsync(
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
                return BusinessServiceSupport.ToUserResponse(updated, actor, _clock.UtcNow);
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
        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
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

                (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
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
                await RecordEventAsync(
                    "user.permissions.changed",
                    actor.Id,
                    "user",
                    target.Id,
                    new Dictionary<string, object?>
                    {
                        ["before"] = new Dictionary<string, object?>
                        {
                            ["permissions"] = BusinessServiceSupport.SummarizeStrings(
                                target.DirectPermissionKeys),
                            ["access_version"] = target.AccessVersion,
                        },
                        ["after"] = new Dictionary<string, object?>
                        {
                            ["permissions"] = BusinessServiceSupport.SummarizeStrings(requestedKeys),
                            ["access_version"] = updated.AccessVersion,
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
                return BusinessServiceSupport.ToUserResponse(updated, actor, _clock.UtcNow);
            },
            cancellationToken);
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

        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                await RequireSettingsAsync(true, cancellationToken).ConfigureAwait(false);
                (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
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
                    await EnsureAdministrationAvailableAsync(cancellationToken).ConfigureAwait(false);
                }

                await RecordEventAsync(
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
                return BusinessServiceSupport.ToUserResponse(updated, actor, now);
            },
            cancellationToken);
    }

    public Task<UserResponse> ApproveAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken) =>
        BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.UsersManage);
                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                User updated = target with { IsApproved = true, UpdatedAt = _clock.UtcNow };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                await RecordEventAsync(
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
                return BusinessServiceSupport.ToUserResponse(updated, actor, _clock.UtcNow);
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

        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
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
                await RecordEventAsync(
                    "user.sessions.revoked",
                    actor.Id,
                    "user",
                    target.Id,
                    new Dictionary<string, object?> { ["scope"] = "all" },
                    cancellationToken,
                    "warning").ConfigureAwait(false);
                return BusinessServiceSupport.ToUserResponse(updated, actor, now);
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
        User initialActor = await RequireActorAsync(
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

    public Task UpdateLocationAsync(
        int userId,
        LocationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _locationValidator.Validate(request).ThrowIfInvalid();
        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                User user = await _users.FindAsync(userId, true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ApiException.Unauthorized(
                        "INVALID_SESSION",
                        "Session invalid or expired");
                if (!BusinessServiceSupport.IsAuthenticatable(user))
                {
                    throw ApiException.Unauthorized(
                        "INVALID_SESSION",
                        "Session invalid or expired");
                }

                DateTime now = _clock.UtcNow;
                GeoPlace place = _geo.Resolve(request.Latitude, request.Longitude);
                User updated = user with
                {
                    LastLatitude = request.Latitude,
                    LastLongitude = request.Longitude,
                    LastLocationAccuracy = request.Accuracy,
                    LastLocationAt = now,
                    LocationRetentionUntil = now.AddHours(_retention.PreciseLocationHours),
                    Location = place.Place ?? user.Location,
                    Timezone = place.Timezone ?? user.Timezone,
                    UpdatedAt = now,
                };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<ExportDocument> ExportAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        UserQuery validationQuery = query with { Limit = 100, Offset = 0 };
        _queryValidator.Validate(validationQuery).ThrowIfInvalid();
        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                User actor = await RequireActorAsync(
                    actorUserId,
                    PermissionKeys.UsersExport,
                    false,
                    cancellationToken).ConfigureAwait(false);
                IReadOnlyList<User> users = await _users.ListForExportAsync(
                    query,
                    DomainLimits.MaxExportRows + 1,
                    cancellationToken).ConfigureAwait(false);
                if (users.Count > DomainLimits.MaxExportRows)
                {
                    throw new ApiException(
                        413,
                        "EXPORT_TOO_LARGE",
                        $"The export exceeds {DomainLimits.MaxExportRows:N0} rows; narrow the filters and retry");
                }

                bool includeLocation = actor.EffectivePermissionKeys.Contains(PermissionKeys.UsersManage);
                IEnumerable<IReadOnlyList<string>> csvRows = BuildCsvRows(users, includeLocation);
                CsvExportWriter.EnsureWithinLimit(
                    csvRows,
                    _exportMaxBytes,
                    cancellationToken);
                await RecordEventAsync(
                    "users.exported",
                    actor.Id,
                    "user_collection",
                    null,
                    new Dictionary<string, object?>
                    {
                        ["exported_count"] = users.Count,
                        ["filters_applied"] = query.Search is not null ||
                            query.RoleId is not null ||
                            query.Status is not null ||
                            query.Online is not null,
                    },
                    cancellationToken).ConfigureAwait(false);
                return new ExportDocument(
                    "users.csv",
                    "text/csv; charset=utf-8",
                    (destination, writeCancellationToken) => CsvExportWriter.WriteAsync(
                        destination,
                        csvRows,
                        writeCancellationToken));
            },
            cancellationToken);
    }

    private async Task ValidateBulkRoleAsync(
        int actorUserId,
        int roleId,
        CancellationToken cancellationToken)
    {
        await BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                await RequireSettingsAsync(true, cancellationToken).ConfigureAwait(false);
                Role role = await _roles.FindAsync(roleId, true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ApiException.BadRequest("UNKNOWN_ROLE", "No such role");
                User actor = await RequireActorAsync(
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
        BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                await RequireSettingsAsync(true, cancellationToken).ConfigureAwait(false);
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

                (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
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

                await EnsureAdministrationAvailableAsync(cancellationToken).ConfigureAwait(false);
                string action = ToBulkAction(request.Action);
                string eventType = request.Action switch
                {
                    BulkUserActionContract.Activate => "user.status.changed",
                    BulkUserActionContract.Deactivate => "user.status.changed",
                    BulkUserActionContract.Approve => "user.approved",
                    BulkUserActionContract.AssignRole => "user.role.changed",
                    _ => throw new ArgumentOutOfRangeException(nameof(request), request.Action, "Unknown action"),
                };
                await RecordEventAsync(
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

    private async Task<User> RequireActorAsync(
        int actorUserId,
        string? permission,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        User? actor = await _users.FindAsync(actorUserId, forUpdate, cancellationToken)
            .ConfigureAwait(false);
        if (!BusinessServiceSupport.IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        if (permission is not null)
        {
            RbacHierarchyPolicy.RequirePermission(actor!, permission);
        }

        return actor!;
    }

    private async Task<User> FindUserAsync(
        int userId,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        await _users.FindAsync(userId, forUpdate, cancellationToken).ConfigureAwait(false)
            ?? throw ApiException.NotFound("User not found");

    private async Task<(User Actor, User Target)> LoadActorAndTargetForUpdateAsync(
        int actorUserId,
        int targetUserId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == targetUserId)
        {
            User same = await FindUserAsync(actorUserId, true, cancellationToken)
                .ConfigureAwait(false);
            if (!BusinessServiceSupport.IsAuthenticatable(same))
            {
                throw ApiException.Forbidden(
                    "FORBIDDEN",
                    "Your account can no longer perform this action");
            }

            return (same, same);
        }

        int firstId = Math.Min(actorUserId, targetUserId);
        int secondId = Math.Max(actorUserId, targetUserId);
        User? first = await _users.FindAsync(firstId, true, cancellationToken)
            .ConfigureAwait(false);
        User? second = await _users.FindAsync(secondId, true, cancellationToken)
            .ConfigureAwait(false);
        User? actor = actorUserId == firstId ? first : second;
        User? target = targetUserId == firstId ? first : second;
        if (!BusinessServiceSupport.IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        return (actor!, target ?? throw ApiException.NotFound("User not found"));
    }

    private async Task<SiteSettings> RequireSettingsAsync(
        bool forUpdate,
        CancellationToken cancellationToken) =>
        await _settings.GetAsync(forUpdate, cancellationToken).ConfigureAwait(false)
            ?? throw BusinessServiceSupport.SettingsNotInitialized();

    private async Task EnsureAdministrationAvailableAsync(CancellationToken cancellationToken)
    {
        if (!await _health.HasReachableAdministratorAsync(cancellationToken).ConfigureAwait(false))
        {
            throw ApiException.Conflict(
                "ADMIN_ACCESS_STRANDED",
                "This change would leave no reachable administrator or bootstrap path");
        }
    }

    private Task<long> RecordEventAsync(
        string eventType,
        int actorUserId,
        string targetType,
        object? targetId,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken,
        string severity = "info") =>
        _securityEvents.RecordAsync(
            BusinessServiceSupport.SecurityEvent(
                _requestContextAccessor,
                eventType,
                actorUserId,
                targetType,
                targetId,
                metadata,
                severity),
            cancellationToken);

    private static User ApplyProfilePatch(User user, ProfileUpdateRequest request)
    {
        var updated = user with
        {
            DisplayName = Apply(user.DisplayName, request.DisplayName),
            Title = Apply(user.Title, request.Title),
            Department = Apply(user.Department, request.Department),
            Phone = Apply(user.Phone, request.Phone),
            Location = Apply(user.Location, request.Location),
            Timezone = Apply(user.Timezone, request.Timezone),
            Bio = Apply(user.Bio, request.Bio),
            AccentColor = Apply(user.AccentColor, request.AccentColor),
        };
        if (!request.UiPreferences.HasValue)
        {
            return updated;
        }

        return updated with
        {
            UiPreferences = request.UiPreferences.Value is null
                ? null
                : UiPreferencesPolicy.ApplyPatch(user.UiPreferences, request.UiPreferences.Value),
        };
    }

    private static string? Apply(string? current, PatchField<string?> patch) =>
        patch.HasValue ? patch.Value : current;

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

    private static Dictionary<string, object?> BulkSnapshot(User user) =>
        new Dictionary<string, object?>
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

    private static IEnumerable<IReadOnlyList<string>> BuildCsvRows(
        IReadOnlyList<User> users,
        bool includeLocation)
    {
        yield return
        [
            "id", "email", "display_name", "role", "status", "title", "department",
            "location", "created_at", "last_login_at",
        ];
        foreach (User user in users)
        {
            yield return
            [
                user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                user.Email,
                user.DisplayName ?? string.Empty,
                user.RoleName,
                user.Status.ToString().ToLowerInvariant(),
                user.Title ?? string.Empty,
                user.Department ?? string.Empty,
                includeLocation ? user.Location ?? string.Empty : string.Empty,
                BusinessServiceSupport.IsoTimestamp(user.CreatedAt),
                BusinessServiceSupport.IsoTimestamp(user.LastLoginAt),
            ];
        }
    }
}
