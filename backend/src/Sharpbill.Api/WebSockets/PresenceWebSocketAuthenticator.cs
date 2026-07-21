using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sharpbill.Api.Authentication;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Users;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.WebSockets;

public sealed record PresenceWebSocketSession(
    int UserId,
    Guid Jti,
    DateTime IssuedAt);

public sealed record PresenceWebSocketAuthenticationResult
{
    private PresenceWebSocketAuthenticationResult(
        PresenceWebSocketSession? session,
        UserResponse? user,
        string? failureCode,
        string? failureMessage)
    {
        Session = session;
        User = user;
        FailureCode = failureCode;
        FailureMessage = failureMessage;
    }

    public PresenceWebSocketSession? Session { get; }
    public UserResponse? User { get; }
    public string? FailureCode { get; }
    public string? FailureMessage { get; }
    public bool IsAuthenticated => Session is not null && User is not null;

    public static PresenceWebSocketAuthenticationResult Success(
        PresenceWebSocketSession session,
        UserResponse user) => new(session, user, null, null);

    public static PresenceWebSocketAuthenticationResult Failure(
        string code,
        string message) => new(null, null, code, message);
}

public interface IPresenceWebSocketAuthenticator
{
    Task<PresenceWebSocketAuthenticationResult> AuthenticateAsync(
        HttpContext context,
        CancellationToken cancellationToken);

    Task<SessionValidationResult> RevalidateAsync(
        PresenceWebSocketSession session,
        CancellationToken cancellationToken);
}

/// <summary>
/// Validates WebSocket sessions in child scopes so the request that owns a long-lived socket
/// never owns a database session or a pooled MySQL connection.
/// </summary>
public sealed partial class PresenceWebSocketAuthenticator(
    IServiceScopeFactory scopeFactory,
    SessionTokenReader tokenReader,
    IOptions<SharpbillOptions> options,
    ILogger<PresenceWebSocketAuthenticator> logger) : IPresenceWebSocketAuthenticator
{
    private readonly SharpbillOptions _options = options.Value;

    public async Task<PresenceWebSocketAuthenticationResult> AuthenticateAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string cookieName = _options.IsLocal
            ? _options.Session.LocalCookieName
            : _options.Session.ProductionCookieName;
        if (!context.Request.Cookies.TryGetValue(cookieName, out string? rawToken) ||
            string.IsNullOrWhiteSpace(rawToken))
        {
            return PresenceWebSocketAuthenticationResult.Failure(
                "NOT_AUTHENTICATED",
                "Not signed in");
        }

        SessionTokenReadResult read = tokenReader.Read(rawToken);
        if (!read.IsValid)
        {
            if (read.ValidationException is not null)
            {
                LogInvalidSession(logger, read.ValidationException);
            }

            return PresenceWebSocketAuthenticationResult.Failure(
                "INVALID_SESSION",
                "Session invalid or expired");
        }

        ValidatedSessionToken token = read.Token!;
        var session = new PresenceWebSocketSession(token.UserId, token.Jti, token.IssuedAt);
        SessionValidationResult validation = await RevalidateAsync(session, cancellationToken)
            .ConfigureAwait(false);
        return validation.IsValid
            ? PresenceWebSocketAuthenticationResult.Success(session, validation.User!)
            : PresenceWebSocketAuthenticationResult.Failure(
                validation.FailureCode ?? "INVALID_SESSION",
                validation.FailureMessage ?? "Session invalid or expired");
    }

    public async Task<SessionValidationResult> RevalidateAsync(
        PresenceWebSocketSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        ISessionService sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
        return await sessions.ValidateAsync(
            session.UserId,
            session.Jti,
            session.IssuedAt,
            cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Information,
        Message = "Presence WebSocket session token validation failed")]
    private static partial void LogInvalidSession(ILogger logger, Exception exception);
}
