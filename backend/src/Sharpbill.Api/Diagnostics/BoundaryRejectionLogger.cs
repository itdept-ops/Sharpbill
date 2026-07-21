namespace Sharpbill.Api.Diagnostics;

/// <summary>Emits structured evidence when the HTTP request boundary rejects input.</summary>
public sealed partial class BoundaryRejectionLogger(
    ILogger<BoundaryRejectionLogger> logger,
    TimeProvider timeProvider)
{
    private const int MaximumPartitions = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly object _gate = new();
    private readonly Dictionary<string, SampleWindow> _windows = new(StringComparer.Ordinal);
    private readonly PriorityQueue<Expiration, long> _expirations = new();
    private long _suppressedTotal;

    public long SuppressedTotal => Interlocked.Read(ref _suppressedTotal);

    public void Record(HttpContext context, string boundary, string errorCode, int statusCode)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? clientIp = Truncate(context.Connection.RemoteIpAddress?.ToString(), 45);
        if (!ShouldEmit(boundary, clientIp, out int suppressedSincePrevious))
        {
            _ = Interlocked.Increment(ref _suppressedTotal);
            return;
        }

        LogRejection(
            logger,
            "request_boundary_rejected",
            Truncate(context.TraceIdentifier, 64) ?? string.Empty,
            Truncate(boundary, 64) ?? string.Empty,
            Truncate(errorCode, 80) ?? string.Empty,
            statusCode,
            Truncate(context.Request.Method, 10) ?? string.Empty,
            Truncate(context.Request.Path.Value, 255),
            clientIp,
            suppressedSincePrevious);
    }

    private bool ShouldEmit(string boundary, string? clientIp, out int suppressedSincePrevious)
    {
        string key = $"{boundary}:{clientIp ?? "unknown"}";
        lock (_gate)
        {
            long now = timeProvider.GetTimestamp();
            if (_windows.TryGetValue(key, out SampleWindow current))
            {
                if (current.ExpiresAt > now)
                {
                    _windows[key] = current with { Suppressed = current.Suppressed + 1 };
                    suppressedSincePrevious = 0;
                    return false;
                }

                _windows.Remove(key);
                suppressedSincePrevious = current.Suppressed;
            }
            else
            {
                suppressedSincePrevious = 0;
            }

            Prune(now);
            if (_windows.Count >= MaximumPartitions)
            {
                return false;
            }

            long duration = checked((long)Math.Ceiling(
                Window.TotalSeconds * timeProvider.TimestampFrequency));
            long expiresAt = checked(now + duration);
            _windows[key] = new SampleWindow(expiresAt, 0);
            _expirations.Enqueue(new Expiration(key, expiresAt), expiresAt);
            return true;
        }
    }

    private void Prune(long now)
    {
        while (_expirations.TryPeek(out Expiration expiration, out long expiresAt) && expiresAt <= now)
        {
            _ = _expirations.Dequeue();
            if (_windows.TryGetValue(expiration.Key, out SampleWindow current) &&
                current.ExpiresAt == expiration.ExpiresAt)
            {
                _windows.Remove(expiration.Key);
            }
        }
    }

    [LoggerMessage(
        EventId = 1100,
        Level = LogLevel.Warning,
        Message = "{Event} boundary {Boundary} rejected {Method} {Path} with {ErrorCode}/{StatusCode} for request {RequestId} client {ClientIp}; {SuppressedSincePrevious} similar events were suppressed")]
    private static partial void LogRejection(
        ILogger logger,
        string @event,
        string requestId,
        string boundary,
        string errorCode,
        int statusCode,
        string method,
        string? path,
        string? clientIp,
        int suppressedSincePrevious);

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, maximumLength)];

    private readonly record struct SampleWindow(long ExpiresAt, int Suppressed);
    private readonly record struct Expiration(string Key, long ExpiresAt);
}
