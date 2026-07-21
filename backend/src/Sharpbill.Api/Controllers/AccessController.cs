using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Access;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Controllers;

[Route("api")]
[Authorize(Policy = PermissionKeys.RolesManage)]
public sealed class AccessController(IRoleService roleService, IPermissionService permissionService) : SharpbillControllerBase
{
    [HttpGet("permissions")]
    public async Task<ActionResult<IReadOnlyList<PermissionResponse>>> PermissionsAsync(CancellationToken cancellationToken) =>
        Ok(await permissionService.ListAsync(ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpPost("permissions")]
    public async Task<ActionResult<PermissionResponse>> CreatePermissionAsync(
        PermissionCreateRequest request,
        CancellationToken cancellationToken)
    {
        PermissionResponse created = await permissionService.CreateAsync(
            ActorUserId,
            request,
            cancellationToken).ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpGet("roles")]
    public async Task<ActionResult<IReadOnlyList<RoleResponse>>> RolesAsync(CancellationToken cancellationToken) =>
        Ok(await roleService.ListAsync(ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpPost("roles")]
    public async Task<ActionResult<RoleResponse>> CreateRoleAsync(
        RoleCreateRequest request,
        CancellationToken cancellationToken)
    {
        RoleResponse created = await roleService.CreateAsync(ActorUserId, request, cancellationToken).ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("roles/{roleId:int}")]
    public async Task<ActionResult<RoleResponse>> UpdateRoleAsync(
        int roleId,
        RoleUpdateRequest request,
        CancellationToken cancellationToken) =>
        Ok(await roleService.UpdateAsync(roleId, ActorUserId, request, cancellationToken).ConfigureAwait(false));

    [HttpDelete("roles/{roleId:int}")]
    public async Task<IActionResult> DeleteRoleAsync(
        int roleId,
        [FromQuery(Name = "expected_version")] int? expectedVersion,
        CancellationToken cancellationToken)
    {
        await roleService.DeleteAsync(roleId, ActorUserId, expectedVersion, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
