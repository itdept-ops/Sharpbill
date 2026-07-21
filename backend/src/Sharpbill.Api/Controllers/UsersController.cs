using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Controllers;

[Route("api/users")]
[Authorize]
public sealed class UsersController(IUserService userService, ISessionService sessionService) : SharpbillControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionKeys.UsersRead)]
    public async Task<ActionResult<UserListResponse>> ListAsync(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery(Name = "role_id")] int? roleId = null,
        [FromQuery] bool? online = null,
        CancellationToken cancellationToken = default) =>
        Ok(await userService.ListAsync(
            new UserQuery { Limit = limit, Offset = offset, Search = search, Status = status, RoleId = roleId, Online = online },
            ActorUserId,
            cancellationToken).ConfigureAwait(false));

    [HttpGet("export.csv")]
    [Authorize(Policy = PermissionKeys.UsersExport)]
    public async Task<IActionResult> ExportAsync(
        [FromQuery] string? search = null,
        [FromQuery] string? status = null,
        [FromQuery(Name = "role_id")] int? roleId = null,
        [FromQuery] bool? online = null,
        CancellationToken cancellationToken = default)
    {
        var query = new UserQuery { Limit = 10_000, Search = search, Status = status, RoleId = roleId, Online = online };
        Application.Common.ExportDocument export =
            await userService.ExportAsync(query, ActorUserId, cancellationToken).ConfigureAwait(false);
        return File(export.Content.ToArray(), export.ContentType, export.FileName);
    }

    [HttpPost("bulk")]
    [Authorize(Policy = PermissionKeys.UsersManage)]
    public async Task<ActionResult<BulkActionResponse>> BulkAsync(
        BulkActionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.BulkAsync(ActorUserId, request, cancellationToken).ConfigureAwait(false));

    [HttpGet("{userId:int}")]
    public async Task<ActionResult<UserResponse>> GetAsync(int userId, CancellationToken cancellationToken) =>
        Ok(await userService.GetAsync(userId, ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpPatch("{userId:int}/profile")]
    public async Task<ActionResult<UserResponse>> UpdateProfileAsync(
        int userId,
        ProfileUpdateRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.UpdateProfileAsync(userId, ActorUserId, request, cancellationToken).ConfigureAwait(false));

    [HttpPatch("{userId:int}/role")]
    [Authorize(Policy = PermissionKeys.UsersManage)]
    public async Task<ActionResult<UserResponse>> AssignRoleAsync(
        int userId,
        RoleAssignRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.AssignRoleAsync(userId, ActorUserId, request, cancellationToken).ConfigureAwait(false));

    [HttpPut("{userId:int}/permissions")]
    [Authorize(Policy = PermissionKeys.UsersManage)]
    public async Task<ActionResult<UserResponse>> SetPermissionsAsync(
        int userId,
        PermissionGrantRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.SetPermissionsAsync(userId, ActorUserId, request, cancellationToken).ConfigureAwait(false));

    [HttpPatch("{userId:int}/status")]
    [Authorize(Policy = PermissionKeys.UsersManage)]
    public async Task<ActionResult<UserResponse>> SetStatusAsync(
        int userId,
        StatusUpdateRequest request,
        CancellationToken cancellationToken) =>
        Ok(await userService.SetStatusAsync(userId, ActorUserId, request, cancellationToken).ConfigureAwait(false));

    [HttpPost("{userId:int}/approve")]
    [Authorize(Policy = PermissionKeys.UsersManage)]
    public async Task<ActionResult<UserResponse>> ApproveAsync(int userId, CancellationToken cancellationToken) =>
        Ok(await userService.ApproveAsync(userId, ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpPost("{userId:int}/kick")]
    [Authorize(Policy = PermissionKeys.PresenceKick)]
    public async Task<ActionResult<UserResponse>> KickAsync(int userId, CancellationToken cancellationToken) =>
        Ok(await userService.KickAsync(userId, ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpGet("{userId:int}/sessions")]
    [Authorize(Policy = PermissionKeys.UsersRead)]
    public async Task<ActionResult<IReadOnlyList<SessionResponse>>> SessionsAsync(
        int userId,
        CancellationToken cancellationToken) =>
        Ok(await sessionService.ListAsync(
            ActorUserId,
            userId,
            includeDeviceDetails: true,
            currentJti: null,
            cancellationToken).ConfigureAwait(false));

    [HttpDelete("{userId:int}/sessions/{sessionId:int}")]
    [Authorize(Policy = PermissionKeys.PresenceKick)]
    public async Task<IActionResult> RevokeSessionAsync(
        int userId,
        int sessionId,
        CancellationToken cancellationToken)
    {
        if (userId == ActorUserId)
        {
            throw Application.Common.ApiException.BadRequest(
                "CANNOT_MODIFY_SELF",
                "Use the personal sessions endpoint");
        }

        await sessionService.RevokeAsync(ActorUserId, userId, sessionId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
