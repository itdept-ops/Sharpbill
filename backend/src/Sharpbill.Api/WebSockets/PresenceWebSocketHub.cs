using System.Net.WebSockets;
using System.Text.Json;
using Sharpbill.Contracts.Operations;

namespace Sharpbill.Api.WebSockets;

public sealed partial class PresenceWebSocketHub(ILogger<PresenceWebSocketHub> logger) : IDisposable
{
    private const int MaximumConnections = 500;
    private const int MaximumConnectionsPerUser = 5;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _broadcastGate = new(1, 1);
    private readonly List<PresenceConnection> _connections = [];

    internal int ConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    public async Task<bool> AddAsync(PresenceConnection connection, CancellationToken cancellationToken)
    {
        List<PresenceConnection> evicted = [];
        lock (_gate)
        {
            if (_connections.Count >= MaximumConnections)
            {
                return false;
            }

            PresenceConnection[] existing = _connections
                .Where(candidate => candidate.UserId == connection.UserId)
                .OrderBy(static candidate => candidate.ConnectedAt)
                .ToArray();
            int excess = existing.Length - MaximumConnectionsPerUser + 1;
            if (excess > 0)
            {
                evicted.AddRange(existing.Take(excess));
                _connections.RemoveAll(evicted.Contains);
            }

            _connections.Add(connection);
        }

        try
        {
            await CloseManyAsync(evicted, WebSocketCloseStatus.NormalClosure, CancellationToken.None)
                .ConfigureAwait(false);
            await BroadcastAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            lock (_gate)
            {
                _connections.Remove(connection);
            }

            connection.RequestDisconnect();
            await BroadcastBestEffortAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task RemoveAsync(PresenceConnection connection)
    {
        lock (_gate)
        {
            _connections.Remove(connection);
        }

        connection.RequestDisconnect();
        await BroadcastBestEffortAsync().ConfigureAwait(false);
    }

    public async Task BroadcastAsync(CancellationToken cancellationToken)
    {
        await _broadcastGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PresenceConnection[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _connections];
            }

            if (snapshot.Length == 0)
            {
                return;
            }

            PresenceSocketUser[] roster = snapshot
                .GroupBy(static connection => connection.UserId)
                .Select(static group => group.Last())
                .Select(static connection => new PresenceSocketUser
                {
                    Id = connection.UserId,
                    DisplayName = connection.DisplayName,
                    Role = connection.Role,
                })
                .ToArray();

            Task<bool>[] sends = snapshot.Select(connection =>
                SendAsync(connection, new PresenceSocketMessage
                {
                    Count = roster.Length,
                    Online = connection.CanView ? roster : null,
                }, cancellationToken)).ToArray();
            bool[] delivered = await Task.WhenAll(sends).ConfigureAwait(false);
            PresenceConnection[] dead = snapshot.Where((_, index) => !delivered[index]).ToArray();
            if (dead.Length > 0)
            {
                lock (_gate)
                {
                    _connections.RemoveAll(dead.Contains);
                }

                await CloseManyAsync(dead, WebSocketCloseStatus.InternalServerError, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _broadcastGate.Release();
        }
    }

    private async Task<bool> SendAsync(
        PresenceConnection connection,
        PresenceSocketMessage message,
        CancellationToken _)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await connection.SendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await connection.Socket.SendAsync(
                    JsonSerializer.SerializeToUtf8Bytes(message),
                    WebSocketMessageType.Text,
                    WebSocketMessageFlags.EndOfMessage,
                    timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                connection.SendGate.Release();
            }

            return true;
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or
                                          ObjectDisposedException)
        {
            LogSendFailure(logger, connection.UserId, exception);
            return false;
        }
    }

    private static async Task CloseManyAsync(
        IEnumerable<PresenceConnection> connections,
        WebSocketCloseStatus status,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(connections.Select(connection => CloseAsync(connection, status, cancellationToken)))
            .ConfigureAwait(false);
    }

    private static async Task CloseAsync(
        PresenceConnection connection,
        WebSocketCloseStatus status,
        CancellationToken cancellationToken)
    {
        connection.RequestDisconnect();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            if (connection.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await connection.Socket.CloseAsync(status, string.Empty, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or
                                          ObjectDisposedException)
        {
            // Best-effort transport close; the connection is already absent from the roster.
        }
    }

    private async Task BroadcastBestEffortAsync()
    {
        using var timeout = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await BroadcastAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // Cleanup must not inherit request cancellation or hold shutdown indefinitely.
        }
    }

    public void Dispose()
    {
        PresenceConnection[] connections;
        lock (_gate)
        {
            connections = [.. _connections];
            _connections.Clear();
        }

        foreach (PresenceConnection connection in connections)
        {
            connection.RequestDisconnect();
        }

        _broadcastGate.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(
        EventId = 1300,
        Level = LogLevel.Debug,
        Message = "Presence WebSocket send failed for user {UserId}")]
    private static partial void LogSendFailure(ILogger logger, int userId, Exception exception);
}

public sealed class PresenceConnection : IDisposable
{
    private int _disposed;

    public required WebSocket Socket { get; init; }
    public int UserId { get; init; }
    public string? DisplayName { get; set; }
    public required string Role { get; set; }
    public bool CanView { get; set; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Disconnect { get; } = new();
    public SemaphoreSlim SendGate { get; } = new(1, 1);

    public void RequestDisconnect()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            Disconnect.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent request owner has already completed disposal.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Disconnect.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Defensive only; disposal is idempotent for callers.
        }

        Disconnect.Dispose();
        SendGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
