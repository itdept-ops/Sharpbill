using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Health;

namespace Sharpbill.Api.Controllers;

[Route("api/health")]
[AllowAnonymous]
public sealed class HealthController(IHealthService healthService) : ControllerBase
{
    [HttpGet("live")]
    [DisableRateLimiting]
    [ApiExplorerSettings(IgnoreApi = true)]
    public ActionResult<LivenessResponse> Live() => Ok(healthService.GetLiveness());

    [HttpGet("ready")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult<ReadinessResponse>> ReadyAsync(CancellationToken cancellationToken)
    {
        (ReadinessResponse response, bool isReady) =
            await healthService.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
        return StatusCode(isReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable, response);
    }

    [HttpGet]
    public Task<ActionResult<ReadinessResponse>> Health(CancellationToken cancellationToken) =>
        ReadyAsync(cancellationToken);
}
