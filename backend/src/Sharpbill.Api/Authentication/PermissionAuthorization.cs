using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Sharpbill.Api.Controllers;
using Sharpbill.Api.Errors;
using Sharpbill.Api.WebSockets;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Authentication;

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (context.User.HasClaim(SharpbillClaimTypes.Permission, requirement.Permission))
        {
            context.Succeed(requirement);
        }
        else if (context.Resource is HttpContext httpContext)
        {
            httpContext.Items["Sharpbill.Authorization.MissingPermission"] = requirement.Permission;
        }

        return Task.CompletedTask;
    }
}

public sealed class SharpbillAuthorizationResultHandler(IPrivilegedDenialRecorder denialRecorder)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            string? missing = context.Items["Sharpbill.Authorization.MissingPermission"] as string;
            await denialRecorder.RecordAsync(context, StatusCodes.Status403Forbidden, "FORBIDDEN")
                .ConfigureAwait(false);
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "FORBIDDEN",
                missing is null ? "Forbidden" : $"Missing permission: {missing}",
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (authorizeResult.Challenged)
        {
            string code = context.Items[SessionAuthenticationDefaults.FailureCodeItem] as string
                ?? "NOT_AUTHENTICATED";
            string message = context.Items[SessionAuthenticationDefaults.FailureMessageItem] as string ??
                code switch
                {
                    "SESSION_REVOKED" => "This session was signed out",
                    "INVALID_SESSION" => "Session invalid or expired",
                    _ => "Not signed in",
                };
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                code,
                message,
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _fallback.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }
}

public static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddSharpbillAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<SessionTokenReader>();
        services.AddSingleton<IPresenceWebSocketAuthenticator, PresenceWebSocketAuthenticator>();
        services.AddAuthentication(SessionAuthenticationDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
                SessionAuthenticationDefaults.Scheme,
                static _ => { });
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder(
                    SessionAuthenticationDefaults.Scheme)
                .RequireAuthenticatedUser()
                .Build();

            foreach (string permission in PermissionKeys.BuiltIn)
            {
                options.AddPolicy(permission, policy =>
                {
                    policy.AddAuthenticationSchemes(SessionAuthenticationDefaults.Scheme);
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new PermissionRequirement(permission));
                });
            }
        });
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, SharpbillAuthorizationResultHandler>();
        return services;
    }
}
