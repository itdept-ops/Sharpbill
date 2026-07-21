using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sharpbill.Api.Authentication;
using Sharpbill.Api.WebSockets;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Users;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Services.Identity;
using SharpbillSessionOptions = Sharpbill.Infrastructure.Configuration.SessionOptions;

namespace Sharpbill.IntegrationTests.WebSockets;

public sealed class PresenceWebSocketHardeningTests
{
    private static readonly DateTime FixedNow = DateTime.UtcNow;

    [Fact]
    public async Task MoreThanPoolSizeAuthenticationChecksDisposeEveryChildScopeAsync()
    {
        const int simultaneousChecks = 24;
        var tracker = new ScopeTracker();
        SharpbillOptions options = CreateOptions();
        var services = new ServiceCollection();
        services.AddSingleton(tracker);
        services.AddScoped<ISessionService, TrackingSessionService>();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        var reader = new SessionTokenReader(Options.Create(options), new FixedClock(FixedNow));
        var authenticator = new PresenceWebSocketAuthenticator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            reader,
            Options.Create(options),
            NullLogger<PresenceWebSocketAuthenticator>.Instance);
        SessionToken token = new SessionJwtIssuer(Options.Create(options)).Issue(
            42,
            Guid.NewGuid(),
            FixedNow);

        Task<PresenceWebSocketAuthenticationResult>[] handshakes = Enumerable
            .Range(0, simultaneousChecks)
            .Select(_ => authenticator.AuthenticateAsync(CreateContext(token.Value), CancellationToken.None))
            .ToArray();
        PresenceWebSocketAuthenticationResult[] results = await Task.WhenAll(handshakes);

        Assert.All(results, result => Assert.True(result.IsAuthenticated));
        Assert.Equal(simultaneousChecks, tracker.Created);
        Assert.Equal(simultaneousChecks, tracker.Disposed);
        Assert.Equal(0, tracker.Active);
        Assert.True(tracker.MaximumActive > 10);

        Task<SessionValidationResult>[] rechecks = results
            .Select(result => authenticator.RevalidateAsync(result.Session!, CancellationToken.None))
            .ToArray();
        SessionValidationResult[] recheckResults = await Task.WhenAll(rechecks);

        Assert.All(recheckResults, result => Assert.True(result.IsValid));
        Assert.Equal(simultaneousChecks * 2, tracker.Created);
        Assert.Equal(simultaneousChecks * 2, tracker.Disposed);
        Assert.Equal(0, tracker.Active);
    }

    [Fact]
    public async Task CancelledAdmissionRollsBackRosterMutationAsync()
    {
        using var hub = new PresenceWebSocketHub(NullLogger<PresenceWebSocketHub>.Instance);
        using var connection = CreateConnection(new TestWebSocket());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => hub.AddAsync(connection, cancelled.Token));

        Assert.Equal(0, hub.ConnectionCount);
        Assert.True(connection.Disconnect.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposedTransportIsRemovedWithoutPoisoningBroadcastAsync()
    {
        using var hub = new PresenceWebSocketHub(NullLogger<PresenceWebSocketHub>.Instance);
        using var connection = CreateConnection(new TestWebSocket(throwOnSend: true));

        Assert.True(await hub.AddAsync(connection, CancellationToken.None));

        Assert.Equal(0, hub.ConnectionCount);
        Assert.True(connection.Disconnect.IsCancellationRequested);
        await hub.BroadcastAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancellingAdmissionMidBroadcastDoesNotEvictExistingRosterAsync()
    {
        using var hub = new PresenceWebSocketHub(NullLogger<PresenceWebSocketHub>.Instance);
        var existingSocket = new TestWebSocket();
        using var existing = CreateConnection(existingSocket);
        using var incoming = CreateConnection(new TestWebSocket(), userId: 43);
        Assert.True(await hub.AddAsync(existing, CancellationToken.None));
        existingSocket.BlockNextSend();
        using var requestCancellation = new CancellationTokenSource();

        Task<bool> admission = hub.AddAsync(incoming, requestCancellation.Token);
        await existingSocket.SendStarted.WaitAsync(TimeSpan.FromSeconds(1));
        requestCancellation.Cancel();
        existingSocket.ReleaseSend();

        Assert.True(await admission);
        Assert.Equal(2, hub.ConnectionCount);
        Assert.False(existing.Disconnect.IsCancellationRequested);
    }

    private static DefaultHttpContext CreateContext(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"session={token}";
        return context;
    }

    private static PresenceConnection CreateConnection(WebSocket socket, int userId = 42) => new()
    {
        Socket = socket,
        UserId = userId,
        DisplayName = "Sharpbill Admin",
        Role = "Admin",
        CanView = true,
    };

    private static SharpbillOptions CreateOptions() => new()
    {
        AppEnvironment = "local",
        Session = new SharpbillSessionOptions
        {
            ActiveSecret = "websocket-test-secret-0000000000000000000000000000",
            Issuer = "sharpbill",
            Audience = "sharpbill-web",
            LifetimeHours = 8,
            LocalCookieName = "session",
            ProductionCookieName = "__Host-session",
        },
    };

    private sealed class ScopeTracker
    {
        private int _active;
        private int _created;
        private int _disposed;
        private int _maximumActive;

        public int Active => Volatile.Read(ref _active);
        public int Created => Volatile.Read(ref _created);
        public int Disposed => Volatile.Read(ref _disposed);
        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public void OnCreated()
        {
            int active = Interlocked.Increment(ref _active);
            _ = Interlocked.Increment(ref _created);
            int maximum = Volatile.Read(ref _maximumActive);
            while (active > maximum)
            {
                int observed = Interlocked.CompareExchange(ref _maximumActive, active, maximum);
                if (observed == maximum)
                {
                    break;
                }

                maximum = observed;
            }
        }

        public void OnDisposed()
        {
            _ = Interlocked.Decrement(ref _active);
            _ = Interlocked.Increment(ref _disposed);
        }
    }

    private sealed class TrackingSessionService : ISessionService, IAsyncDisposable
    {
        private readonly ScopeTracker _tracker;

        public TrackingSessionService(ScopeTracker tracker)
        {
            _tracker = tracker;
            tracker.OnCreated();
        }

        public Task<SessionValidationResult> ValidateAsync(
            int userId,
            Guid jti,
            DateTime issuedAt,
            CancellationToken cancellationToken) => ValidateAfterDelayAsync(cancellationToken);

        public Task<SessionToken> StartAsync(
            int userId,
            bool legalAccepted,
            string legalBundleVersion,
            RequestContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionResponse>> ListAsync(
            int actorUserId,
            int targetUserId,
            bool includeDeviceDetails,
            Guid? currentJti,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RevokeAsync(
            int actorUserId,
            int targetUserId,
            int sessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RevokeAllAsync(
            int targetUserId,
            DateTime validAfter,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _tracker.OnDisposed();
            return ValueTask.CompletedTask;
        }

        private static async Task<SessionValidationResult> ValidateAfterDelayAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken).ConfigureAwait(false);
            return SessionValidationResult.Valid(new UserResponse
            {
                Id = 42,
                Email = "admin@example.test",
                DisplayName = "Sharpbill Admin",
                Role = "Admin",
                IsActive = true,
                IsApproved = true,
            });
        }
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class TestWebSocket(bool throwOnSend = false) : WebSocket
    {
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private WebSocketState _state = WebSocketState.Open;
        private bool _blockSends;
        private TaskCompletionSource<bool> _sendRelease = CreateCompletionSource();
        private TaskCompletionSource<bool> _sendStarted = CreateCompletionSource();

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public Task SendStarted => _sendStarted.Task;

        public void BlockNextSend()
        {
            _sendStarted = CreateCompletionSource();
            _sendRelease = CreateCompletionSource();
            _blockSends = true;
        }

        public void ReleaseSend()
        {
            _blockSends = false;
            _sendRelease.TrySetResult(true);
        }

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (throwOnSend)
            {
                return Task.FromException(new ObjectDisposedException(nameof(TestWebSocket)));
            }

            if (!_blockSends)
            {
                return Task.CompletedTask;
            }

            _sendStarted.TrySetResult(true);
            return WaitForSendReleaseAsync(cancellationToken);
        }

        private async Task WaitForSendReleaseAsync(CancellationToken cancellationToken) =>
            await _sendRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        private static TaskCompletionSource<bool> CreateCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
