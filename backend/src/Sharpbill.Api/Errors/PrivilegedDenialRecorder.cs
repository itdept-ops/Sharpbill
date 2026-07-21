using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;

namespace Sharpbill.Api.Errors;

public interface IPrivilegedDenialRecorder
{
    Task RecordAsync(HttpContext context, int statusCode, string code);
}

public sealed partial class PrivilegedDenialRecorder(
    ILogger<PrivilegedDenialRecorder> logger) : IPrivilegedDenialRecorder
{
    public async Task RecordAsync(HttpContext context, int statusCode, string code)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!(HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
              HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)) ||
            statusCode is not (StatusCodes.Status403Forbidden or StatusCodes.Status409Conflict or
                StatusCodes.Status428PreconditionRequired) ||
            !int.TryParse(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int actorUserId))
        {
            return;
        }

        try
        {
            // Authentication has already opened the request's scoped database session. Reuse it
            // so a denied call never holds one pool lease while waiting for a second audit lease.
            var events = context.RequestServices.GetRequiredService<ISecurityEventService>();
            string route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ??
                context.Request.Path.Value ?? string.Empty;
            _ = await events.RecordAsync(new SecurityEventWrite
            {
                EventType = "privileged_mutation.denied",
                Outcome = "denied",
                Severity = "warning",
                ActorUserId = actorUserId,
                TargetType = "api_route",
                TargetId = route,
                RequestId = context.TraceIdentifier,
                SourceIp = context.Connection.RemoteIpAddress?.ToString(),
                Metadata = new Dictionary<string, object?>
                {
                    ["method"] = context.Request.Method,
                    ["status_code"] = statusCode,
                    ["code"] = code[..Math.Min(code.Length, 80)],
                },
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFailure(logger, exception, context.TraceIdentifier);
        }
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Unable to persist denied-mutation evidence for request {RequestId}")]
    private static partial void LogFailure(ILogger logger, Exception exception, string requestId);
}
