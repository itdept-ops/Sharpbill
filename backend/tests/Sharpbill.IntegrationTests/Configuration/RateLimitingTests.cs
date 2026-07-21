using Sharpbill.Api.Configuration;

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

    private sealed class ControllableTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan duration) =>
            _ = Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
