using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Access;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Business;

public sealed class PermissionService : IPermissionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _users;
    private readonly IPermissionRepository _permissions;
    private readonly ISecurityEventService _securityEvents;
    private readonly IClock _clock;
    private readonly IRequestContextAccessor _requestContextAccessor;
    private readonly IValidator<PermissionCreateRequest> _validator;

    public PermissionService(
        IUnitOfWork unitOfWork,
        IUserRepository users,
        IPermissionRepository permissions,
        ISecurityEventService securityEvents,
        IClock clock,
        IRequestContextAccessor requestContextAccessor,
        IValidator<PermissionCreateRequest> validator)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _securityEvents = securityEvents ?? throw new ArgumentNullException(nameof(securityEvents));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _requestContextAccessor = requestContextAccessor ??
            throw new ArgumentNullException(nameof(requestContextAccessor));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<IReadOnlyList<PermissionResponse>> ListAsync(
        int actorUserId,
        CancellationToken cancellationToken)
    {
        _ = await RequireActorAsync(actorUserId, false, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Permission> permissions = await _permissions.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        return permissions
            .OrderBy(static permission => permission.Key, StringComparer.Ordinal)
            .Select(BusinessServiceSupport.ToPermissionResponse)
            .ToArray();
    }

    public async Task<PermissionResponse> CreateAsync(
        int actorUserId,
        PermissionCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _validator.Validate(request).ThrowIfInvalid();
        string key = request.Key.Trim().ToLowerInvariant();
        try
        {
            Permission created = await BusinessServiceSupport.InTransactionAsync(
                _unitOfWork,
                async () =>
                {
                    if (await _permissions.FindByKeyAsync(key, cancellationToken)
                        .ConfigureAwait(false) is not null)
                    {
                        throw ApiException.Conflict(
                            "ALREADY_EXISTS",
                            $"Permission '{key}' already exists");
                    }

                    User actor = await RequireActorAsync(actorUserId, true, cancellationToken)
                        .ConfigureAwait(false);
                    DateTime now = _clock.UtcNow;
                    var permission = new Permission
                    {
                        Id = 0,
                        Key = key,
                        Description = request.Description,
                        IsSystem = false,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    int permissionId = await _permissions.AddAsync(permission, cancellationToken)
                        .ConfigureAwait(false);
                    Permission inserted = permission with { Id = permissionId };
                    await _securityEvents.RecordAsync(
                        BusinessServiceSupport.SecurityEvent(
                            _requestContextAccessor,
                            "rbac.permission.created",
                            actor.Id,
                            "permission",
                            permissionId,
                            new Dictionary<string, object?>
                            {
                                ["permission_key"] = key,
                            }),
                        cancellationToken).ConfigureAwait(false);
                    return inserted;
                },
                cancellationToken).ConfigureAwait(false);
            return BusinessServiceSupport.ToPermissionResponse(created);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw ApiException.Conflict(
                "ALREADY_EXISTS",
                $"Permission '{key}' already exists");
        }
    }

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
}
