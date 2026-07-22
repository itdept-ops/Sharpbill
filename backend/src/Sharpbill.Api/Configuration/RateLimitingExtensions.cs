using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Sharpbill.Api.Diagnostics;
using Sharpbill.Api.Errors;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Configuration;

public static class RateLimitingExtensions
{
    public const string ExportPolicyName = "exports";

    private const int MaximumActivePartitions = 10_000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddSharpbillRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var partitionRegistry = new BoundedRateLimitPartitionRegistry(
            MaximumActivePartitions,
            TimeProvider.System);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                CreatePartition(context, partitionRegistry));
            options.AddPolicy<string, ExportConcurrencyRateLimitPolicy>(ExportPolicyName);
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.RequestServices
                    .GetRequiredService<BoundaryRejectionLogger>()
                    .Record(
                        context.HttpContext,
                        "rate_limit",
                        "RATE_LIMITED",
                        StatusCodes.Status429TooManyRequests);
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = Math.Max(
                            1,
                            (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                }

                await ApiErrorWriter.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    "RATE_LIMITED",
                    "Too many requests — slow down.",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            };
        });
        return services;
    }

    internal sealed class ExportConcurrencyRateLimitPolicy(IOptions<SharpbillOptions> options)
        : IRateLimiterPolicy<string>
    {
        private readonly int _permitLimit = options.Value.RequestPipeline.ExportMaxConcurrency;

        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected =>
            RejectExportAsync;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
            RateLimitPartition.GetConcurrencyLimiter(
                ExportPolicyName,
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = _permitLimit,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });

        private static async ValueTask RejectExportAsync(
            OnRejectedContext context,
            CancellationToken cancellationToken)
        {
            context.HttpContext.Response.Headers.RetryAfter = "1";
            context.HttpContext.RequestServices
                .GetRequiredService<BoundaryRejectionLogger>()
                .Record(
                    context.HttpContext,
                    "export_concurrency",
                    "EXPORT_CAPACITY_EXCEEDED",
                    StatusCodes.Status429TooManyRequests);
            await ApiErrorWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status429TooManyRequests,
                "EXPORT_CAPACITY_EXCEEDED",
                "The export capacity is in use; retry shortly.",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static RateLimitPartition<string> CreatePartition(
        HttpContext context,
        BoundedRateLimitPartitionRegistry registry)
    {
        (string bucket, int permitLimit) = Classify(context.Request.Path.Value);
        if (permitLimit == int.MaxValue)
        {
            return RateLimitPartition.GetNoLimiter<string>(bucket);
        }

        IPAddress? address = context.Connection.RemoteIpAddress;
        string key = $"{bucket}:{address?.ToString() ?? "unknown"}";
        if (!registry.TryReserve(key, Window))
        {
            return RateLimitPartition.Get(
                "rate-limit-partition-capacity",
                static _ => new RejectedRateLimiter(Window));
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                Window = Window,
            });
    }

    private static (string Bucket, int PermitLimit) Classify(string? rawPath)
    {
        string path = rawPath ?? string.Empty;
        if (path.Equals("/api/health/live", StringComparison.OrdinalIgnoreCase))
        {
            return ("liveness", int.MaxValue);
        }

        if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/health/ready", StringComparison.OrdinalIgnoreCase))
        {
            return ("readiness", 30);
        }

        if (path.Equals("/api/auth/nonce", StringComparison.OrdinalIgnoreCase))
        {
            return ("nonce", 30);
        }

        if (path.Equals("/api/auth/google", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/auth/microsoft", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/auth/dev", StringComparison.OrdinalIgnoreCase))
        {
            return ("login", 20);
        }

        return path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? ("api", 600)
            : ("non-api", int.MaxValue);
    }

    internal sealed class BoundedRateLimitPartitionRegistry(int maximumPartitions, TimeProvider timeProvider)
    {
        private readonly Dictionary<string, long> _expirations = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private readonly int _maximumPartitions = maximumPartitions > 0
            ? maximumPartitions
            : throw new ArgumentOutOfRangeException(nameof(maximumPartitions));
        private readonly PriorityQueue<Expiration, long> _queue = new();
        private readonly TimeProvider _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));

        public bool TryReserve(string key, TimeSpan lifetime)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

            lock (_gate)
            {
                long now = _timeProvider.GetTimestamp();
                Prune(now);
                if (_expirations.TryGetValue(key, out long current) && current > now)
                {
                    return true;
                }

                if (_expirations.Count >= _maximumPartitions)
                {
                    return false;
                }

                long lifetimeTicks = checked((long)Math.Ceiling(
                    lifetime.TotalSeconds * _timeProvider.TimestampFrequency));
                long expiresAt = checked(now + lifetimeTicks);
                _expirations[key] = expiresAt;
                _queue.Enqueue(new Expiration(key, expiresAt), expiresAt);
                return true;
            }
        }

        private void Prune(long now)
        {
            while (_queue.TryPeek(out Expiration expiration, out long expiresAt) && expiresAt <= now)
            {
                _ = _queue.Dequeue();
                if (_expirations.TryGetValue(expiration.Key, out long current) &&
                    current == expiration.ExpiresAt)
                {
                    _expirations.Remove(expiration.Key);
                }
            }
        }

        private readonly record struct Expiration(string Key, long ExpiresAt);
    }

    private sealed class RejectedRateLimiter(TimeSpan retryAfter) : RateLimiter
    {
        private readonly RateLimitLease _lease = new RejectedLease(retryAfter);
        private long _failedLeases;

        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics GetStatistics() => new()
        {
            CurrentAvailablePermits = 0,
            CurrentQueuedCount = 0,
            TotalFailedLeases = Interlocked.Read(ref _failedLeases),
            TotalSuccessfulLeases = 0,
        };

        protected override RateLimitLease AttemptAcquireCore(int permitCount)
        {
            _ = Interlocked.Increment(ref _failedLeases);
            return _lease;
        }

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(
            int permitCount,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);
            }

            _ = Interlocked.Increment(ref _failedLeases);
            return ValueTask.FromResult(_lease);
        }
    }

    private sealed class RejectedLease(TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (string.Equals(metadataName, MetadataName.RetryAfter.Name, StringComparison.Ordinal))
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
