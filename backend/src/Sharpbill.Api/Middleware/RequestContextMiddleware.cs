using System.Diagnostics;
using System.Text.RegularExpressions;
using Sharpbill.Application.Common;

namespace Sharpbill.Api.Middleware;

public sealed partial class RequestContextMiddleware(RequestDelegate next, ILogger<RequestContextMiddleware> logger)
{
    public const string RequestIdHeader = "X-Request-ID";
    public const string ClientRequestIdHeader = "X-Client-Request-ID";
    private static readonly object ClientRequestIdItemKey = new();

    public async Task InvokeAsync(HttpContext context, IRequestContextAccessor requestContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(context);
        string requestId = Guid.NewGuid().ToString("N");
        string? clientRequestId = SanitizeClientRequestId(context.Request.Headers[RequestIdHeader]);
        context.TraceIdentifier = requestId;
        Activity.Current?.SetTag("http.request_id", requestId);
        if (clientRequestId is not null)
        {
            Activity.Current?.SetTag("http.client_request_id", clientRequestId);
            context.Items[ClientRequestIdItemKey] = clientRequestId;
        }

        requestContextAccessor.Current = new RequestContext
        {
            RequestId = requestId,
            ClientRequestId = clientRequestId,
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
                    if (httpContext.Items.TryGetValue(ClientRequestIdItemKey, out object? clientId))
                    {
                        httpContext.Response.Headers[ClientRequestIdHeader] = (string)clientId!;
                    }

                    httpContext.Response.Headers.CacheControl = "no-store, max-age=0";
                    httpContext.Response.Headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            },
            context);

        var scopeValues = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["request_id"] = requestId,
        };
        if (clientRequestId is not null)
        {
            scopeValues["client_request_id"] = clientRequestId;
        }

        using IDisposable? scope = logger.BeginScope(scopeValues);
        await next(context).ConfigureAwait(false);
    }

    internal static string? GetClientRequestId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ClientRequestIdItemKey, out object? value)
            ? value as string
            : null;
    }

    private static string? SanitizeClientRequestId(string? supplied)
    {
        string? candidate = string.IsNullOrWhiteSpace(supplied) ? null : supplied.Trim();
        return candidate is { Length: <= 64 } && RequestIdPattern().IsMatch(candidate)
            ? candidate
            : null;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();
}
