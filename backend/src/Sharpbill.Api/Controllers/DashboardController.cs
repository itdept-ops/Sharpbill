using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Dashboard;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Controllers;

[Route("api/dashboard")]
[Authorize]
public sealed class DashboardController(IDashboardService dashboardService) : SharpbillControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DashboardResponse>> GetAsync(CancellationToken cancellationToken) =>
        Ok(await dashboardService.GetAsync(ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpGet("analytics")]
    [Authorize(Policy = PermissionKeys.UsersRead)]
    public async Task<ActionResult<AnalyticsResponse>> AnalyticsAsync(CancellationToken cancellationToken) =>
        Ok(await dashboardService.GetAnalyticsAsync(ActorUserId, cancellationToken).ConfigureAwait(false));
}
