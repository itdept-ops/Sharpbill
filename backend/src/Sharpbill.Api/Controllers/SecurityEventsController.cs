using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Sharpbill.Application.Abstractions;
using Sharpbill.Api.Configuration;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Controllers;

[Route("api/admin/security-events")]
[Authorize(Policy = PermissionKeys.SecurityEventsView)]
public sealed class SecurityEventsController(ISecurityEventService securityEventService) : SharpbillControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SecurityEventListResponse>> ListAsync(
        [FromQuery] int limit = 100,
        [FromQuery(Name = "before_id")] long? beforeId = null,
        [FromQuery(Name = "event_type")] string? eventType = null,
        [FromQuery] string? outcome = null,
        [FromQuery] string? severity = null,
        [FromQuery(Name = "actor_user_id")] int? actorUserId = null,
        [FromQuery(Name = "request_id")] string? requestId = null,
        CancellationToken cancellationToken = default) =>
        Ok(await securityEventService.ListAsync(
            Query(limit, beforeId, eventType, outcome, severity, actorUserId, requestId),
            ActorUserId,
            cancellationToken).ConfigureAwait(false));

    [HttpGet("export.csv")]
    [EnableRateLimiting(RateLimitingExtensions.ExportPolicyName)]
    public async Task<IActionResult> ExportAsync(
        [FromQuery] int limit = 1000,
        [FromQuery(Name = "before_id")] long? beforeId = null,
        [FromQuery(Name = "event_type")] string? eventType = null,
        [FromQuery] string? outcome = null,
        [FromQuery] string? severity = null,
        [FromQuery(Name = "actor_user_id")] int? actorUserId = null,
        [FromQuery(Name = "request_id")] string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        Application.Common.ExportDocument export = await securityEventService.ExportAsync(
            Query(limit, beforeId, eventType, outcome, severity, actorUserId, requestId),
            ActorUserId,
            cancellationToken).ConfigureAwait(false);
        return await WriteExportAsync(export, cancellationToken).ConfigureAwait(false);
    }

    private static SecurityEventQuery Query(
        int limit,
        long? beforeId,
        string? eventType,
        string? outcome,
        string? severity,
        int? actorUserId,
        string? requestId) => new()
        {
            Limit = limit,
            BeforeId = beforeId,
            EventType = eventType,
            Outcome = outcome,
            Severity = severity,
            ActorUserId = actorUserId,
            RequestId = requestId,
        };
}
