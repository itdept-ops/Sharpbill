using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Business;

public sealed class RoleService : IRoleService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IPermissionRepository _permissions;
    private readonly ISettingsRepository _settings;
    private readonly ISecurityEventService _securityEvents;
    private readonly IClock _clock;
    private readonly IRequestContextAccessor _requestContextAccessor;
    private readonly IValidator<RoleCreateRequest> _createValidator;
    private readonly IValidator<RoleUpdateRequest> _updateValidator;

    public RoleService(
        IUnitOfWork unitOfWork,
        IUserRepository users,
        IRoleRepository roles,
        IPermissionRepository permissions,
        ISettingsRepository settings,
        ISecurityEventService securityEvents,
        IClock clock,
        IRequestContextAccessor requestContextAccessor,
        IValidator<RoleCreateRequest> createValidator,
        IValidator<RoleUpdateRequest> updateValidator)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _securityEvents = securityEvents ?? throw new ArgumentNullException(nameof(securityEvents));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _requestContextAccessor = requestContextAccessor ??
            throw new ArgumentNullException(nameof(requestContextAccessor));
        _createValidator = createValidator ?? throw new ArgumentNullException(nameof(createValidator));
        _updateValidator = updateValidator ?? throw new ArgumentNullException(nameof(updateValidator));
    }

    public async Task<IReadOnlyList<RoleResponse>> ListAsync(
        int actorUserId,
        CancellationToken cancellationToken)
    {
        _ = await RequireActorAsync(actorUserId, false, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Role> roles = await _roles.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<int, int> counts = await _roles.GetUserCountsAsync(cancellationToken)
            .ConfigureAwait(false);
        return roles.Select(role => BusinessServiceSupport.ToRoleResponse(
            role,
            counts.GetValueOrDefault(role.Id))).ToArray();
    }

    public async Task<RoleResponse> CreateAsync(
        int actorUserId,
        RoleCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _createValidator.Validate(request).ThrowIfInvalid();
        string name = request.Name.Trim();
        string[] keys = RbacHierarchyPolicy.NormalizePermissionKeys(request.PermissionKeys).ToArray();
        try
        {
            Role created = await BusinessServiceSupport.InTransactionAsync(
                _unitOfWork,
                async () =>
                {
                    if (await _roles.FindByNameAsync(name, false, cancellationToken)
                        .ConfigureAwait(false) is not null)
                    {
                        throw ApiException.Conflict(
                            "ALREADY_EXISTS",
                            $"Role '{name}' already exists");
                    }

                    IReadOnlyList<Permission> permissions = await ResolvePermissionsAsync(
                        keys,
                        cancellationToken).ConfigureAwait(false);
                    User actor = await RequireActorAsync(actorUserId, true, cancellationToken)
                        .ConfigureAwait(false);
                    RbacHierarchyPolicy.EnsurePermissionsGrantable(actor, keys);
                    DateTime now = _clock.UtcNow;
                    var role = new Role
                    {
                        Id = 0,
                        Name = name,
                        Description = request.Description,
                        IsSystem = false,
                        Version = 1,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    int roleId = await _roles.AddAsync(role, cancellationToken).ConfigureAwait(false);
                    await _roles.ReplacePermissionsAsync(
                        roleId,
                        permissions.Select(static permission => permission.Id).ToArray(),
                        cancellationToken).ConfigureAwait(false);
                    Role inserted = role with { Id = roleId, Permissions = permissions };
                    await RecordEventAsync(
                        "rbac.role.created",
                        actor.Id,
                        inserted.Id,
                        new Dictionary<string, object?>
                        {
                            ["permissions"] = BusinessServiceSupport.SummarizeStrings(
                                inserted.PermissionKeys),
                        },
                        cancellationToken).ConfigureAwait(false);
                    return inserted;
                },
                cancellationToken).ConfigureAwait(false);
            return BusinessServiceSupport.ToRoleResponse(created, 0);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw ApiException.Conflict("ALREADY_EXISTS", $"Role '{name}' already exists");
        }
    }

    public async Task<RoleResponse> UpdateAsync(
        int roleId,
        int actorUserId,
        RoleUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _updateValidator.Validate(request).ThrowIfInvalid();
        try
        {
            Role updated = await BusinessServiceSupport.InTransactionAsync(
                _unitOfWork,
                async () =>
                {
                    _ = await RequireSettingsAsync(cancellationToken).ConfigureAwait(false);
                    Role role = await _roles.FindAsync(roleId, true, cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw ApiException.NotFound("Role not found");

                    IReadOnlyList<Permission>? requestedPermissions = null;
                    if (request.PermissionKeys.HasValue && request.PermissionKeys.Value is not null)
                    {
                        string[] keys = RbacHierarchyPolicy.NormalizePermissionKeys(
                            request.PermissionKeys.Value).ToArray();
                        requestedPermissions = await ResolvePermissionsAsync(keys, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    User actor = await RequireActorAsync(actorUserId, true, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureRoleMutable(actor, role, request);
                    int? expectedVersion = request.ExpectedVersion.HasValue
                        ? request.ExpectedVersion.Value
                        : null;
                    RbacHierarchyPolicy.RequireVersion(expectedVersion, role.Version, "Role");

                    string? requestedName = request.Name.HasValue ? request.Name.Value : null;
                    string name = requestedName is null ? role.Name : requestedName.Trim();
                    if (!string.Equals(name, role.Name, StringComparison.Ordinal) &&
                        await _roles.FindByNameAsync(name, false, cancellationToken)
                            .ConfigureAwait(false) is not null)
                    {
                        throw ApiException.Conflict(
                            "ALREADY_EXISTS",
                            $"Role '{name}' already exists");
                    }

                    if (requestedPermissions is not null)
                    {
                        RbacHierarchyPolicy.EnsurePermissionsGrantable(
                            actor,
                            requestedPermissions.Select(static permission => permission.Key));
                    }

                    Role changed = role with
                    {
                        Name = name,
                        Description = request.Description.HasValue && request.Description.Value is not null
                            ? request.Description.Value
                            : role.Description,
                        Permissions = requestedPermissions ?? role.Permissions,
                        Version = role.Version + 1,
                        UpdatedAt = _clock.UtcNow,
                    };
                    await _roles.UpdateAsync(changed, cancellationToken).ConfigureAwait(false);
                    if (requestedPermissions is not null)
                    {
                        await _roles.ReplacePermissionsAsync(
                            role.Id,
                            requestedPermissions.Select(static permission => permission.Id).ToArray(),
                            cancellationToken).ConfigureAwait(false);
                    }

                    await RecordEventAsync(
                        "rbac.role.updated",
                        actor.Id,
                        role.Id,
                        RoleUpdateMetadata(role, changed),
                        cancellationToken).ConfigureAwait(false);
                    return changed;
                },
                cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<int, int> counts = await _roles.GetUserCountsAsync(cancellationToken)
                .ConfigureAwait(false);
            return BusinessServiceSupport.ToRoleResponse(
                updated,
                counts.GetValueOrDefault(updated.Id));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            string requestedName = request.Name.HasValue && request.Name.Value is not null
                ? request.Name.Value.Trim()
                : string.Empty;
            throw ApiException.Conflict(
                "ALREADY_EXISTS",
                $"Role '{requestedName}' already exists");
        }
    }

    public Task DeleteAsync(
        int roleId,
        int actorUserId,
        int? expectedVersion,
        CancellationToken cancellationToken) =>
        BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                SiteSettings settings = await RequireSettingsAsync(cancellationToken)
                    .ConfigureAwait(false);
                Role role = await _roles.FindAsync(roleId, true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ApiException.NotFound("Role not found");
                User actor = await RequireActorAsync(actorUserId, true, cancellationToken)
                    .ConfigureAwait(false);
                if (role.IsSystem)
                {
                    throw ApiException.Forbidden(
                        "PROTECTED_ROLE",
                        "System roles cannot be deleted");
                }

                EnsureActorCanModifyRole(actor, role, "delete");
                IReadOnlyDictionary<int, int> counts = await _roles.GetUserCountsAsync(cancellationToken)
                    .ConfigureAwait(false);
                int userCount = counts.GetValueOrDefault(role.Id);
                if (userCount > 0)
                {
                    throw ApiException.Conflict(
                        "ROLE_IN_USE",
                        $"{userCount} user(s) still have this role; reassign them first");
                }

                if (settings.DefaultRoleId == role.Id)
                {
                    throw ApiException.Conflict(
                        "ROLE_IN_USE",
                        "This role is the signup default; select another default role before deleting it");
                }

                RbacHierarchyPolicy.RequireVersion(expectedVersion, role.Version, "Role");
                await RecordEventAsync(
                    "rbac.role.deleted",
                    actor.Id,
                    role.Id,
                    new Dictionary<string, object?>
                    {
                        ["name"] = role.Name,
                        ["description"] = role.Description,
                        ["permissions"] = BusinessServiceSupport.SummarizeStrings(role.PermissionKeys),
                    },
                    cancellationToken).ConfigureAwait(false);
                await _roles.DeleteAsync(role.Id, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task<User> RequireActorAsync(
        int actorUserId,
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

        RbacHierarchyPolicy.RequirePermission(actor!, PermissionKeys.RolesManage);
        return actor!;
    }

    private async Task<SiteSettings> RequireSettingsAsync(CancellationToken cancellationToken) =>
        await _settings.GetAsync(true, cancellationToken).ConfigureAwait(false)
            ?? throw BusinessServiceSupport.SettingsNotInitialized();

    private async Task<IReadOnlyList<Permission>> ResolvePermissionsAsync(
        string[] keys,
        CancellationToken cancellationToken)
    {
        if (keys.Length == 0)
        {
            return [];
        }

        IReadOnlyList<Permission> permissions = await _permissions.FindByKeysAsync(
            keys,
            cancellationToken).ConfigureAwait(false);
        string[] missing = keys
            .Except(permissions.Select(static permission => permission.Key), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw ApiException.BadRequest(
                "UNKNOWN_PERMISSION",
                $"Unknown permissions: {string.Join(", ", missing)}");
        }

        return permissions;
    }

    private static void EnsureRoleMutable(User actor, Role role, RoleUpdateRequest request)
    {
        if (string.Equals(role.Name, SystemRoleNames.Administrator, StringComparison.Ordinal))
        {
            throw ApiException.Forbidden(
                "PROTECTED_ROLE",
                "The admin role cannot be modified");
        }

        if (role.IsSystem && !RbacHierarchyPolicy.IsAdministrator(actor))
        {
            throw ApiException.Forbidden(
                "PROTECTED_ROLE",
                "System roles can only be edited by an admin");
        }

        if (role.IsSystem &&
            request.Name.HasValue &&
            request.Name.Value is not null &&
            !string.Equals(request.Name.Value.Trim(), role.Name, StringComparison.Ordinal))
        {
            throw ApiException.Forbidden(
                "PROTECTED_ROLE",
                "System roles cannot be renamed");
        }

        EnsureActorCanModifyRole(actor, role, "modify");
    }

    private static void EnsureActorCanModifyRole(User actor, Role role, string action)
    {
        if (!RbacHierarchyPolicy.IsAdministrator(actor) &&
            !role.PermissionKeys.IsSubsetOf(actor.EffectivePermissionKeys))
        {
            throw ApiException.Forbidden(
                "INSUFFICIENT_PRIVILEGE",
                $"You cannot {action} a role that grants permissions you do not hold");
        }
    }

    private Task<long> RecordEventAsync(
        string eventType,
        int actorUserId,
        int roleId,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken) =>
        _securityEvents.RecordAsync(
            BusinessServiceSupport.SecurityEvent(
                _requestContextAccessor,
                eventType,
                actorUserId,
                "role",
                roleId,
                metadata),
            cancellationToken);

    private static Dictionary<string, object?> RoleUpdateMetadata(Role before, Role after)
    {
        var changedFields = new List<string>();
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
        {
            changedFields.Add("name");
        }

        if (!string.Equals(before.Description, after.Description, StringComparison.Ordinal))
        {
            changedFields.Add("description");
        }

        if (!before.PermissionKeys.SetEquals(after.PermissionKeys))
        {
            changedFields.Add("permission_keys");
        }

        return new Dictionary<string, object?>
        {
            ["changed_fields"] = changedFields,
            ["before"] = new Dictionary<string, object?>
            {
                ["name"] = before.Name,
                ["description"] = before.Description,
                ["permissions"] = BusinessServiceSupport.SummarizeStrings(before.PermissionKeys),
            },
            ["after"] = new Dictionary<string, object?>
            {
                ["name"] = after.Name,
                ["description"] = after.Description,
                ["permissions"] = BusinessServiceSupport.SummarizeStrings(after.PermissionKeys),
            },
        };
    }
}
