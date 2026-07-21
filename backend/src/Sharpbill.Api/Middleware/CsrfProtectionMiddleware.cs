using Microsoft.Extensions.Options;
using Sharpbill.Api.Diagnostics;
using Sharpbill.Api.Errors;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Middleware;

public sealed class CsrfProtectionMiddleware(
    RequestDelegate next,
    IOptions<SharpbillOptions> options,
    BoundaryRejectionLogger rejectionLogger)
{
    private static readonly HashSet<string> ProvenanceOptionalPaths = new(StringComparer.Ordinal)
    {
        "/api/auth/nonce",
        "/api/auth/google",
        "/api/auth/microsoft",
        "/api/auth/dev",
    };

    private readonly SharpbillOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!RequiresProtection(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        string fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fetchSite, "same-site", StringComparison.OrdinalIgnoreCase))
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        Uri expectedOrigin = ResolveExpectedOrigin(context.Request);
        string? origin = HeaderValue(context.Request.Headers.Origin.ToString());
        string? referer = HeaderValue(context.Request.Headers.Referer.ToString());

        if (origin is not null)
        {
            if (!MatchesOrigin(origin, expectedOrigin))
            {
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            // Origin is authoritative when present; do not let a stale or synthetic Referer
            // override a valid same-origin browser signal.
            await next(context).ConfigureAwait(false);
            return;
        }

        if (referer is not null)
        {
            if (!MatchesOrigin(referer, expectedOrigin))
            {
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        if (string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!_options.IsLocal
            && !ProvenanceOptionalPaths.Contains(context.Request.Path.Value ?? string.Empty)
            && HasSessionCookie(context.Request))
        {
            await RejectAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private bool HasSessionCookie(HttpRequest request) =>
        request.Cookies.ContainsKey(_options.IsLocal
            ? _options.Session.LocalCookieName
            : _options.Session.ProductionCookieName);

    private static bool RequiresProtection(HttpRequest request) =>
        request.Path.StartsWithSegments("/api")
        && (HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method)
            || HttpMethods.IsDelete(request.Method));

    private Uri ResolveExpectedOrigin(HttpRequest request)
    {
        if (!_options.IsLocal && _options.PublicOrigin is not null)
        {
            return new Uri(_options.PublicOrigin, UriKind.Absolute);
        }

        return new Uri($"{request.Scheme}://{request.Host.Value}/", UriKind.Absolute);
    }

    private static bool MatchesOrigin(string value, Uri expected)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate))
        {
            return false;
        }

        return string.Equals(candidate.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
            && EffectivePort(candidate) == EffectivePort(expected);
    }

    private static int EffectivePort(Uri uri) => uri.IsDefaultPort
        ? uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
        : uri.Port;

    private static string? HeaderValue(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task RejectAsync(HttpContext context)
    {
        rejectionLogger.Record(
            context,
            "csrf",
            "CSRF_REJECTED",
            StatusCodes.Status403Forbidden);
        return ApiErrorWriter.WriteAsync(
            context,
            StatusCodes.Status403Forbidden,
            "CSRF_REJECTED",
            "Cross-origin state-changing requests are not allowed",
            cancellationToken: context.RequestAborted);
    }
}
