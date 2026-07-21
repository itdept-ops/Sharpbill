namespace Sharpbill.Api.Errors;

public static class StatusCodePagesExtensions
{
    public static IApplicationBuilder UseSharpbillStatusCodePages(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseStatusCodePages(async statusContext =>
        {
            HttpContext context = statusContext.HttpContext;
            (string code, string message) = context.Response.StatusCode switch
            {
                StatusCodes.Status400BadRequest => ("BAD_REQUEST", "Bad Request"),
                StatusCodes.Status401Unauthorized => ("NOT_AUTHENTICATED", "Not signed in"),
                StatusCodes.Status403Forbidden => ("FORBIDDEN", "Forbidden"),
                StatusCodes.Status404NotFound => ("NOT_FOUND", "Not Found"),
                StatusCodes.Status405MethodNotAllowed => ("METHOD_NOT_ALLOWED", "Method Not Allowed"),
                StatusCodes.Status413PayloadTooLarge => ("CONTENT_TOO_LARGE", "Content Too Large"),
                StatusCodes.Status415UnsupportedMediaType => ("UNSUPPORTED_MEDIA_TYPE", "Unsupported Media Type"),
                StatusCodes.Status429TooManyRequests => ("RATE_LIMITED", "Too Many Requests"),
                _ => ("HTTP_ERROR", "HTTP error"),
            };
            await ApiErrorWriter.WriteAsync(
                context,
                context.Response.StatusCode,
                code,
                message,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        });
    }
}
