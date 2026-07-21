using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using Sharpbill.Api.Authentication;
using Sharpbill.Api.Errors;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.WebSockets;

public static class PresenceWebSocketEndpoint
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumClientPingInterval = TimeSpan.FromSeconds(10);

    public static async Task HandleAsync(
        HttpContext context,
        PresenceWebSocketHub hub,
        IPresenceWebSocketAuthenticator authentication,
        IRequestContextAccessor requestContextAccessor,
        IOptions<SharpbillOptions> options)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!OriginAllowed(context, options.Value))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        PresenceWebSocketAuthenticationResult authenticationResult = await authentication.AuthenticateAsync(
            context,
            context.RequestAborted).ConfigureAwait(false);
        if (!authenticationResult.IsAuthenticated)
        {
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                authenticationResult.FailureCode ?? "NOT_AUTHENTICATED",
                authenticationResult.FailureMessage ?? "Not signed in",
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
            return;
        }

        PresenceWebSocketSession session = authenticationResult.Session!;
        UserResponse user = authenticationResult.User!;
        context.User = SessionPrincipalFactory.Create(user, session.Jti, session.IssuedAt);
        context.Items["Sharpbill.UserId"] = user.Id;
        requestContextAccessor.Current = requestContextAccessor.Current with
        {
            SessionJti = session.Jti,
            SessionUserId = user.Id,
        };
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var connection = new PresenceConnection
        {
            Socket = socket,
            UserId = user.Id,
            DisplayName = user.DisplayName,
            Role = user.Role,
            CanView = user.Permissions.Contains(PermissionKeys.PresenceView, StringComparer.Ordinal),
        };
        bool admitted = false;
        try
        {
            admitted = await hub.AddAsync(connection, context.RequestAborted).ConfigureAwait(false);
            if (!admitted)
            {
                await CloseWithTimeoutAsync(
                    socket,
                    (WebSocketCloseStatus)1013,
                    "Presence capacity reached").ConfigureAwait(false);
                return;
            }

            DateTimeOffset nextAuthentication = DateTimeOffset.UtcNow.Add(RecheckInterval);
            DateTimeOffset? lastClientPing = null;
            var receiveBuffer = new byte[16];
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted,
                    connection.Disconnect.Token);
                TimeSpan untilAuthentication = nextAuthentication - DateTimeOffset.UtcNow;
                iteration.CancelAfter(untilAuthentication > TimeSpan.Zero ? untilAuthentication : TimeSpan.Zero);
                try
                {
                    WebSocketReceiveResult result = await socket.ReceiveAsync(receiveBuffer, iteration.Token)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage ||
                        result.Count != 4 ||
                        !receiveBuffer.AsSpan(0, result.Count).SequenceEqual("ping"u8))
                    {
                        await CloseWithTimeoutAsync(
                            socket,
                            WebSocketCloseStatus.InvalidMessageType,
                            "Only the ping message is supported").ConfigureAwait(false);
                        break;
                    }

                    DateTimeOffset receivedAt = DateTimeOffset.UtcNow;
                    if (lastClientPing is { } previousPing &&
                        receivedAt - previousPing < MinimumClientPingInterval)
                    {
                        await CloseWithTimeoutAsync(
                            socket,
                            WebSocketCloseStatus.PolicyViolation,
                            "Ping rate exceeded").ConfigureAwait(false);
                        break;
                    }

                    lastClientPing = receivedAt;
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested &&
                                                         !connection.Disconnect.IsCancellationRequested)
                {
                    // Periodic policy recheck.
                }

                if (connection.Disconnect.IsCancellationRequested)
                {
                    break;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now < nextAuthentication)
                {
                    continue;
                }

                nextAuthentication = now.Add(RecheckInterval);
                SessionValidationResult validation = await authentication.RevalidateAsync(
                    session,
                    context.RequestAborted).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    await CloseWithTimeoutAsync(
                        socket,
                        WebSocketCloseStatus.PolicyViolation,
                        "Session revoked").ConfigureAwait(false);
                    break;
                }

                UserResponse current = validation.User!;

                bool changed = connection.DisplayName != current.DisplayName || connection.Role != current.Role ||
                    connection.CanView != current.Permissions.Contains(PermissionKeys.PresenceView, StringComparer.Ordinal);
                connection.DisplayName = current.DisplayName;
                connection.Role = current.Role;
                connection.CanView = current.Permissions.Contains(PermissionKeys.PresenceView, StringComparer.Ordinal);
                if (changed)
                {
                    await hub.BroadcastAsync(context.RequestAborted).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or
                                          ObjectDisposedException)
        {
            // Disconnects and host cancellation are normal terminal states.
        }
        finally
        {
            if (admitted)
            {
                await hub.RemoveAsync(connection).ConfigureAwait(false);
            }
        }
    }

    private static async Task CloseWithTimeoutAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(status, description, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or
                                          ObjectDisposedException)
        {
            // Best effort: the request and socket are already terminating.
        }
    }

    private static bool OriginAllowed(HttpContext context, SharpbillOptions options)
    {
        string? origin = context.Request.Headers.Origin.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        Uri? expected;
        if (!options.IsLocal)
        {
            _ = Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out expected);
        }
        else
        {
            _ = Uri.TryCreate($"{context.Request.Scheme}://{context.Request.Host}", UriKind.Absolute, out expected);
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out Uri? supplied) &&
            expected is not null && supplied.UserInfo.Length == 0 && supplied.AbsolutePath is "/" &&
            supplied.Query.Length == 0 && supplied.Fragment.Length == 0 &&
            string.Equals(supplied.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(supplied.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
            EffectivePort(supplied) == EffectivePort(expected);
    }

    private static int EffectivePort(Uri value) => value.IsDefaultPort
        ? string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80
        : value.Port;
}
