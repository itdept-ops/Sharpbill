namespace Sharpbill.Api.Middleware;

public sealed class ApiSecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.OnStarting(
                static state =>
                {
                    HttpResponse response = ((HttpContext)state).Response;
                    response.Headers.XContentTypeOptions = "nosniff";
                    response.Headers.XFrameOptions = "DENY";
                    response.Headers["Referrer-Policy"] = "no-referrer";
                    response.Headers["Permissions-Policy"] = "geolocation=(self)";
                    return Task.CompletedTask;
                },
                context);
        }

        return next(context);
    }
}
