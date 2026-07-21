using Sharpbill.Api.Errors;
using Sharpbill.Api.Diagnostics;

namespace Sharpbill.Api.Middleware;

public sealed class LoginContentTypeMiddleware(RequestDelegate next, BoundaryRejectionLogger rejectionLogger)
{
    private static readonly HashSet<PathString> JsonRequiredPaths =
    [
        new("/api/auth/google"),
        new("/api/auth/microsoft"),
        new("/api/auth/dev"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? contentType = context.Request.ContentType;
        if (HttpMethods.IsPost(context.Request.Method) && JsonRequiredPaths.Contains(context.Request.Path) &&
            (contentType is null || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)))
        {
            rejectionLogger.Record(
                context,
                "content_type",
                "UNSUPPORTED_MEDIA_TYPE",
                StatusCodes.Status415UnsupportedMediaType);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "UNSUPPORTED_MEDIA_TYPE",
                "Content-Type must be application/json",
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
