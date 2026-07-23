using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Users;

namespace Sharpbill.Infrastructure.Services.Business;

public sealed class UserService : IUserService
{
    private readonly IUserQueryService _queries;
    private readonly IUserProfileService _profiles;
    private readonly IUserAccessService _access;
    private readonly IUserLifecycleService _lifecycle;

    public UserService(
        IUserQueryService queries,
        IUserProfileService profiles,
        IUserAccessService access,
        IUserLifecycleService lifecycle)
    {
        _queries = queries ?? throw new ArgumentNullException(nameof(queries));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
    }

    public Task<UserListResponse> ListAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken) =>
        _queries.ListAsync(query, actorUserId, cancellationToken);

    public Task<UserResponse> GetAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken) =>
        _queries.GetAsync(userId, actorUserId, cancellationToken);

    public Task<UserResponse> UpdateProfileAsync(
        int userId,
        int actorUserId,
        ProfileUpdateRequest request,
        CancellationToken cancellationToken) =>
        _profiles.UpdateProfileAsync(userId, actorUserId, request, cancellationToken);

    public Task<UserResponse> AssignRoleAsync(
        int userId,
        int actorUserId,
        RoleAssignRequest request,
        CancellationToken cancellationToken) =>
        _access.AssignRoleAsync(userId, actorUserId, request, cancellationToken);

    public Task<UserResponse> SetPermissionsAsync(
        int userId,
        int actorUserId,
        PermissionGrantRequest request,
        CancellationToken cancellationToken) =>
        _access.SetPermissionsAsync(userId, actorUserId, request, cancellationToken);

    public Task<UserResponse> SetStatusAsync(
        int userId,
        int actorUserId,
        StatusUpdateRequest request,
        CancellationToken cancellationToken) =>
        _lifecycle.SetStatusAsync(userId, actorUserId, request, cancellationToken);

    public Task<UserResponse> ApproveAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken) =>
        _lifecycle.ApproveAsync(userId, actorUserId, cancellationToken);

    public Task<UserResponse> KickAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken) =>
        _lifecycle.KickAsync(userId, actorUserId, cancellationToken);

    public Task<BulkActionResponse> BulkAsync(
        int actorUserId,
        BulkActionRequest request,
        CancellationToken cancellationToken) =>
        _lifecycle.BulkAsync(actorUserId, request, cancellationToken);

    public Task UpdateLocationAsync(
        int userId,
        LocationUpdateRequest request,
        CancellationToken cancellationToken) =>
        _profiles.UpdateLocationAsync(userId, request, cancellationToken);

    public Task<ExportDocument> ExportAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken) =>
        _queries.ExportAsync(query, actorUserId, cancellationToken);
}
