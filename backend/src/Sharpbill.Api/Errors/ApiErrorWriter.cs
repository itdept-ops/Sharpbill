using System.Text.Json;

namespace Sharpbill.Api.Errors;

public static class ApiErrorWriter
{
    public static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        object? errors = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        object detail = errors is null
            ? new { code, message }
            : new { code, message, errors };

        return context.Response.WriteAsJsonAsync(
            new { detail },
            cancellationToken: cancellationToken);
    }
}
