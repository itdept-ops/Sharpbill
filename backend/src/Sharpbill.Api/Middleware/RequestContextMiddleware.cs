using System.Diagnostics;
using System.Text.RegularExpressions;
using Sharpbill.Application.Common;

namespace Sharpbill.Api.Middleware;

public sealed partial class RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> logger)
{
    public const string RequestIdHeader = "X-Request-ID";

    public async Task InvokeAsync(HttpContext context, IRequestContextAccessor requestContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(context);
        string requestId = ResolveRequestId(context.Request.Headers[RequestIdHeader]);
        context.TraceIdentifier = requestId;
        Activity.Current?.SetTag("http.request_id", requestId);
        requestContextAccessor.Current = new RequestContext
        {
            RequestId = requestId,
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : null,
        };

        context.Response.OnStarting(
            static state =>
            {
                HttpContext httpContext = (HttpContext)state;
                if (httpContext.Request.Path.StartsWithSegments("/api"))
                {
                    httpContext.Response.Headers[RequestIdHeader] = httpContext.TraceIdentifier;
                    httpContext.Response.Headers.CacheControl = "no-store, max-age=0";
                    httpContext.Response.Headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            },
            context);

        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["request_id"] = requestId,
        });
        await next(context).ConfigureAwait(false);
    }

    private static string ResolveRequestId(string? supplied)
    {
        string? candidate = string.IsNullOrWhiteSpace(supplied) ? null : supplied.Trim();
        return candidate is { Length: <= 64 } && RequestIdPattern().IsMatch(candidate)
            ? candidate
            : Guid.NewGuid().ToString("N");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();
}
