using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Sharpbill.Api.Configuration;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.IntegrationTests.Configuration;

public sealed class RateLimitingTests
{
    [Fact]
    public void PartitionRegistryFailsClosedAtCapacityAndRecoversAfterExpiry()
    {
        var time = new ControllableTimeProvider();
        var registry = new RateLimitingExtensions.BoundedRateLimitPartitionRegistry(2, time);

        Assert.True(registry.TryReserve("api:192.0.2.1", TimeSpan.FromMinutes(1)));
        Assert.True(registry.TryReserve("api:192.0.2.2", TimeSpan.FromMinutes(1)));
        Assert.False(registry.TryReserve("api:192.0.2.3", TimeSpan.FromMinutes(1)));

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.True(registry.TryReserve("api:192.0.2.3", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ExportPolicyRejectsWithoutQueueingAtTheProcessWideLimit()
    {
        var options = Options.Create(new SharpbillOptions
        {
            RequestPipeline = new RequestPipelineOptions { ExportMaxConcurrency = 2 },
        });
        var policy = new RateLimitingExtensions.ExportConcurrencyRateLimitPolicy(options);
        using PartitionedRateLimiter<HttpContext> limiter =
            PartitionedRateLimiter.Create<HttpContext, string>(policy.GetPartition);
        var context = new DefaultHttpContext();

        using RateLimitLease first = limiter.AttemptAcquire(context);
        using RateLimitLease second = limiter.AttemptAcquire(new DefaultHttpContext());
        using RateLimitLease rejected = limiter.AttemptAcquire(new DefaultHttpContext());

        Assert.True(first.IsAcquired);
        Assert.True(second.IsAcquired);
        Assert.False(rejected.IsAcquired);
        Assert.Equal(0, limiter.GetStatistics(context)?.CurrentQueuedCount);
    }

    private sealed class ControllableTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan duration) =>
            _ = Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
