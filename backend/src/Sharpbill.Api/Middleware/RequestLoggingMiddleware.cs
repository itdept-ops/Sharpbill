using System.Security.Claims;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Services.Operations;

namespace Sharpbill.Api.Middleware;

public sealed partial class RequestLoggingMiddleware(
    RequestDelegate next,
    IRequestLogBuffer buffer,
    TimeProvider timeProvider,
    ILogger<RequestLoggingMiddleware> logger)
{
    private static readonly string[] SkippedPrefixes =
    [
        "/api/health",
        "/api/ws",
        "/api/docs",
        "/api/openapi",
        "/api/presence",
        "/api/auth/config",
        "/api/auth/me",
        "/api/auth/nonce",
        "/api/legal/manifest",
    ];

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        long startedAt = timeProvider.GetTimestamp();
        context.Response.OnCompleted(
            static state =>
            {
                var (middleware, httpContext, startTimestamp) =
                    ((RequestLoggingMiddleware Middleware, HttpContext Context, long StartTimestamp))state;
                middleware.Record(httpContext, httpContext.Response.StatusCode, startTimestamp);
                return Task.CompletedTask;
            },
            (this, context, startedAt));
        return next(context);
    }

    private void Record(HttpContext context, int statusCode, long startedAt)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        if (HttpMethods.IsOptions(context.Request.Method) ||
            !path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            SkippedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        int? userId = int.TryParse(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            out int parsedUserId) ? parsedUserId : null;
        string method = context.Request.Method[..Math.Min(context.Request.Method.Length, 10)];
        string boundedPath = path[..Math.Min(path.Length, 255)];
        string? ipAddress = Truncate(context.Connection.RemoteIpAddress?.ToString(), 45);
        string? clientRequestId = RequestContextMiddleware.GetClientRequestId(context);
        double durationMilliseconds = Math.Round(
            timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
            2,
            MidpointRounding.AwayFromZero);
        LogRequest(
            logger,
            "http_request",
            context.TraceIdentifier,
            method,
            boundedPath,
            statusCode,
            durationMilliseconds,
            userId,
            ipAddress,
            clientRequestId);
        bool accepted = buffer.TryWrite(new RequestLog
        {
            Id = 0,
            Method = method,
            Path = boundedPath,
            UserId = userId,
            IpAddress = ipAddress,
            StatusCode = statusCode,
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
        if (!accepted)
        {
            LogDrop(logger, context.TraceIdentifier);
        }
    }

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Warning,
        Message = "Request activity record dropped for request {RequestId}")]
    private static partial void LogDrop(ILogger logger, string requestId);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Information,
        Message = "{Event} {Method} {Path} returned {StatusCode} in {DurationMs} ms for request {RequestId} client request {ClientRequestId} user {UserId} client {ClientIp}")]
    private static partial void LogRequest(
        ILogger logger,
        string @event,
        string requestId,
        string method,
        string path,
        int statusCode,
        double durationMs,
        int? userId,
        string? clientIp,
        string? clientRequestId);

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, maximumLength)];
}
