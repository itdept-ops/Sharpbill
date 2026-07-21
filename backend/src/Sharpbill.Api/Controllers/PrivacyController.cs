using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Controllers;

[Route("api/privacy")]
[Authorize]
public sealed class PrivacyController(IPrivacyService privacyService) : SharpbillControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PrivacyStatusResponse>> GetAsync(CancellationToken cancellationToken) =>
        Ok(await privacyService.GetAsync(ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpDelete("location")]
    public async Task<IActionResult> DeleteLocationAsync(CancellationToken cancellationToken)
    {
        await privacyService.DeleteLocationAsync(ActorUserId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("erasure-request")]
    public async Task<ActionResult<PrivacyStatusResponse>> RequestErasureAsync(CancellationToken cancellationToken) =>
        Ok(await privacyService.RequestErasureAsync(ActorUserId, ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpDelete("erasure-request")]
    public async Task<ActionResult<PrivacyStatusResponse>> CancelErasureAsync(CancellationToken cancellationToken) =>
        Ok(await privacyService.CancelErasureAsync(ActorUserId, ActorUserId, cancellationToken).ConfigureAwait(false));
}

[Route("api/admin/privacy")]
[Authorize(Policy = PermissionKeys.PrivacyManage)]
public sealed class PrivacyAdministrationController(IPrivacyService privacyService) : SharpbillControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PrivacyAdminStatusResponse>> GetAsync(CancellationToken cancellationToken) =>
        Ok(await privacyService.GetAdministrationAsync(ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpPut("hold")]
    public async Task<ActionResult<PrivacyAdminStatusResponse>> HoldAsync(
        RetentionHoldUpdateRequest request,
        CancellationToken cancellationToken) =>
        Ok(await privacyService.UpdateHoldAsync(ActorUserId, request, cancellationToken).ConfigureAwait(false));

    [HttpPost("users/{userId:int}/erasure-request")]
    public async Task<ActionResult<PrivacyStatusResponse>> RequestUserErasureAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        if (userId == ActorUserId)
        {
            throw ApiException.BadRequest("CANNOT_MODIFY_SELF", "Use the personal privacy endpoint");
        }

        return Ok(await privacyService.RequestErasureAsync(
            ActorUserId,
            userId,
            cancellationToken).ConfigureAwait(false));
    }

    [HttpDelete("users/{userId:int}/erasure-request")]
    public async Task<ActionResult<PrivacyStatusResponse>> CancelUserErasureAsync(
        int userId,
        CancellationToken cancellationToken) =>
        Ok(await privacyService.CancelErasureAsync(ActorUserId, userId, cancellationToken).ConfigureAwait(false));
}
