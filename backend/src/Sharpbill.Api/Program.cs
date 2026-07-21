using Microsoft.Extensions.Logging.Console;
using Microsoft.AspNetCore.RateLimiting;
using Sharpbill.Api.Authentication;
using Sharpbill.Api.Configuration;
using Sharpbill.Api.Diagnostics;
using Sharpbill.Api.Errors;
using Sharpbill.Api.Middleware;
using Sharpbill.Api.WebSockets;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Workers;

if (args is ["--health-check", var healthCheckUrl])
{
    return await HealthCheckCommand.RunAsync(healthCheckUrl).ConfigureAwait(false);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LoggingConfiguration.ParseMinimumLevel(builder.Configuration));
builder.Logging.AddJsonConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});
builder.Services.AddSharpbillConfiguration(builder.Configuration);
builder.Services.AddSharpbillRuntime();
builder.Services.AddSharpbillWorkers();
builder.Services.AddSingleton<PresenceWebSocketHub>();
builder.Services.AddSharpbillControllers(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddSingleton<IPrivilegedDenialRecorder, PrivilegedDenialRecorder>();
builder.Services.AddSingleton<BoundaryRejectionLogger>();
builder.Services.AddSharpbillRateLimiting();
builder.Services.AddSharpbillAuthentication();

WebApplication app = builder.Build();
SharpbillOptions runtimeOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SharpbillOptions>>().Value;
app.UseSharpbillForwardedHeaders();
app.UseMiddleware<RequestContextMiddleware>();
app.UseExceptionHandler();
app.UseSharpbillStatusCodePages();
app.UseMiddleware<ApiSecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseMiddleware<CsrfProtectionMiddleware>();
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.UseMiddleware<LoginContentTypeMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/api/ws/presence", PresenceWebSocketEndpoint.HandleAsync)
    // WebSocket authentication is performed by the endpoint in disposable child scopes.
    .AllowAnonymous()
    .ExcludeFromDescription();
if (runtimeOptions.IsLocal)
{
    app.MapGet("/api/docs", () => Results.Content(
            """
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Sharpbill API</title></head>
            <body><main><h1>Sharpbill API</h1><p><a href="/api/openapi.json">OpenAPI document</a></p></main></body></html>
            """,
            "text/html"))
        .AllowAnonymous()
        .ExcludeFromDescription();
    app.MapOpenApi("/api/openapi.json")
        .AllowAnonymous();
}

await app.RunAsync().ConfigureAwait(false);
return 0;

public partial class Program;
