using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Controllers;

[Route("api/presence")]
[Authorize]
public sealed class PresenceController(IPresenceService presenceService) : SharpbillControllerBase
{
    [HttpGet("online")]
    [Authorize(Policy = PermissionKeys.PresenceView)]
    public async Task<ActionResult<PresenceResponse>> OnlineAsync(CancellationToken cancellationToken) =>
        Ok(await presenceService.GetOnlineAsync(ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpPost("heartbeat")]
    public async Task<ActionResult<HeartbeatResponse>> HeartbeatAsync(CancellationToken cancellationToken) =>
        Ok(await presenceService.HeartbeatAsync(ActorUserId, cancellationToken).ConfigureAwait(false));
}
