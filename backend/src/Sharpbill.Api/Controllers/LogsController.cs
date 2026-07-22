using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Controllers;

[Route("api/admin/logs")]
[Authorize(Policy = PermissionKeys.LogsView)]
public sealed class LogsController(IRequestLogService requestLogService) : SharpbillControllerBase
{
    [HttpGet("metrics")]
    public ActionResult<RequestLogMetricsResponse> Metrics() => Ok(requestLogService.GetMetrics());

    [HttpGet]
    public async Task<ActionResult<RequestLogListResponse>> ListAsync(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        [FromQuery(Name = "before_id")] long? beforeId = null,
        [FromQuery(Name = "include_total")] bool includeTotal = false,
        [FromQuery] string? search = null,
        [FromQuery] string? method = null,
        [FromQuery(Name = "user_id")] int? userId = null,
        CancellationToken cancellationToken = default) =>
        Ok(await requestLogService.ListAsync(
            new RequestLogQuery
            {
                Limit = limit,
                Offset = offset,
                BeforeId = beforeId,
                IncludeTotal = includeTotal,
                Search = search,
                Method = method,
                UserId = userId,
            },
            ActorUserId,
            cancellationToken).ConfigureAwait(false));
}
