using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Workers;

namespace Sharpbill.IntegrationTests.Workers;

public sealed class RequestLogBufferWorkerTests
{
    [Fact]
    public async Task NormalShutdownDrainsEveryAcceptedRequestLogAsync()
    {
        var repository = new CapturingRequestLogRepository();
        var services = new ServiceCollection();
        services.AddScoped<IRequestLogRepository>(_ => repository);
        services.AddScoped<IUnitOfWork, StubUnitOfWork>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = Options.Create(new SharpbillOptions
        {
            RequestPipeline = new RequestPipelineOptions
            {
                RequestLogQueueCapacity = 512,
                RequestLogShutdownTimeoutSeconds = 5,
            },
        });
        using var worker = new RequestLogBufferWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<RequestLogBufferWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        for (int index = 0; index < 250; index++)
        {
            Assert.True(worker.TryWrite(new RequestLog
            {
                Id = 0,
                Method = "GET",
                Path = $"/api/test/{index}",
                StatusCode = 200,
                CreatedAt = DateTime.UtcNow,
            }));
        }

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(stopTimeout.Token);

        RequestLogMetricsResponse metrics = worker.GetMetrics();
        Assert.Equal(250, repository.Items.Count);
        Assert.Equal(250, metrics.EnqueuedTotal);
        Assert.Equal(250, metrics.PersistedTotal);
        Assert.Equal(0, metrics.DroppedTotal);
        Assert.Equal(0, metrics.QueueDepth);
        Assert.False(metrics.Running);
    }

    private sealed class CapturingRequestLogRepository : IRequestLogRepository
    {
        public List<RequestLog> Items { get; } = [];

        public Task<RequestLogListResponse> ListAsync(
            RequestLogQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddBatchAsync(
            IReadOnlyCollection<RequestLog> requestLogs,
            CancellationToken cancellationToken)
        {
            Items.AddRange(requestLogs);
            return Task.CompletedTask;
        }

        public Task<int> PruneAsync(
            DateTime cutoff,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubUnitOfWork : IUnitOfWork
    {
        public Task BeginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
