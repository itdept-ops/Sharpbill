using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Sharpbill.Api.Errors;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Authentication;

public sealed partial class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<SharpbillOptions> sharpbillOptions,
    SessionTokenReader tokenReader,
    ISessionService sessionService,
    IRequestContextAccessor requestContextAccessor)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    private readonly SharpbillOptions _options = sharpbillOptions.Value;
    private readonly ISessionService _sessionService = sessionService;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        bool allowsAnonymous = Context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        if (allowsAnonymous && !Request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string cookieName = _options.IsLocal
            ? _options.Session.LocalCookieName
            : _options.Session.ProductionCookieName;
        if (!Request.Cookies.TryGetValue(cookieName, out string? rawToken) || string.IsNullOrWhiteSpace(rawToken))
        {
            Context.Items[SessionAuthenticationDefaults.FailureCodeItem] = "NOT_AUTHENTICATED";
            return AuthenticateResult.NoResult();
        }

        try
        {
            SessionTokenReadResult read = tokenReader.Read(rawToken);
            if (!read.IsValid)
            {
                if (read.ValidationException is not null)
                {
                    LogInvalidSession(Logger, read.ValidationException);
                }

                return InvalidSession();
            }

            ValidatedSessionToken token = read.Token!;
            requestContextAccessor.Current = requestContextAccessor.Current with
            {
                SessionJti = token.Jti,
                SessionUserId = token.UserId,
            };
            if (Request.Path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase))
            {
                // Logout accepts a validly signed token even if the server-side session is
                // missing or revoked, allowing that attempt to remain auditable.
                return AuthenticateResult.NoResult();
            }

            SessionValidationResult validation = await _sessionService.ValidateAsync(
                token.UserId,
                token.Jti,
                token.IssuedAt,
                Context.RequestAborted).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                Context.Items[SessionAuthenticationDefaults.FailureCodeItem] = validation.FailureCode;
                Context.Items[SessionAuthenticationDefaults.FailureMessageItem] = validation.FailureMessage;
                return AuthenticateResult.Fail(validation.FailureMessage ?? "Session invalid or expired");
            }

            UserResponse user = validation.User!;

            Context.Items["Sharpbill.UserId"] = user.Id;
            return AuthenticateResult.Success(
                new AuthenticationTicket(
                    SessionPrincipalFactory.Create(user, token.Jti, token.IssuedAt),
                    Scheme.Name));
        }
        catch (ArgumentException exception)
        {
            LogInvalidSession(Logger, exception);
            return InvalidSession();
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        string code = Context.Items[SessionAuthenticationDefaults.FailureCodeItem] as string
            ?? "NOT_AUTHENTICATED";
        string message = Context.Items[SessionAuthenticationDefaults.FailureMessageItem] as string ??
            code switch
            {
                "SESSION_REVOKED" => "This session was signed out",
                "INVALID_SESSION" => "Session invalid or expired",
                _ => "Not signed in",
            };
        return ApiErrorWriter.WriteAsync(Context, StatusCodes.Status401Unauthorized, code, message);
    }

    private AuthenticateResult InvalidSession()
    {
        Context.Items[SessionAuthenticationDefaults.FailureCodeItem] = "INVALID_SESSION";
        Context.Items[SessionAuthenticationDefaults.FailureMessageItem] = "Session invalid or expired";
        return AuthenticateResult.Fail("Session invalid or expired.");
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Session token validation failed")]
    private static partial void LogInvalidSession(ILogger logger, Exception exception);
}
