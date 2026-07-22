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
            TimeProvider.System,
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
        Assert.Equal(0, metrics.RejectedTotal);
        Assert.Equal(0, metrics.LostAfterEnqueueTotal);
        Assert.Equal(0, metrics.OutstandingTotal);
        Assert.Equal(0, metrics.QueueDepth);
        Assert.False(metrics.LossDetected);
        Assert.NotNull(metrics.LastEnqueuedAt);
        Assert.NotNull(metrics.LastPersistedAt);
        Assert.Null(metrics.LastDroppedAt);
        Assert.Null(metrics.LastErrorAt);
        Assert.False(metrics.Running);
    }

    [Fact]
    public async Task SaturationSeparatesRejectedEventsFromAcceptedWorkAsync()
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
                RequestLogQueueCapacity = 1,
                RequestLogShutdownTimeoutSeconds = 5,
            },
        });
        using var worker = new RequestLogBufferWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<RequestLogBufferWorker>.Instance);

        Assert.True(worker.TryWrite(CreateLog("/api/accepted")));
        Assert.False(worker.TryWrite(CreateLog("/api/rejected")));
        RequestLogMetricsResponse saturated = worker.GetMetrics();
        Assert.Equal(1, saturated.EnqueuedTotal);
        Assert.Equal(1, saturated.RejectedTotal);
        Assert.Equal(1, saturated.DroppedTotal);
        Assert.Equal(0, saturated.LostAfterEnqueueTotal);
        Assert.Equal(1, saturated.OutstandingTotal);
        Assert.True(saturated.LossDetected);
        Assert.NotNull(saturated.LastDroppedAt);

        await worker.StartAsync(CancellationToken.None);
        await worker.FlushAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        RequestLogMetricsResponse drained = worker.GetMetrics();
        Assert.Single(repository.Items);
        Assert.Equal(1, drained.PersistedTotal);
        Assert.Equal(0, drained.OutstandingTotal);
        Assert.Equal(1, drained.RejectedTotal);
    }

    [Fact]
    public async Task PersistenceFailureRecordsAcceptedLossAndErrorTimestampsAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestLogRepository, FailingRequestLogRepository>();
        services.AddScoped<IUnitOfWork, StubUnitOfWork>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = Options.Create(new SharpbillOptions
        {
            RequestPipeline = new RequestPipelineOptions
            {
                RequestLogQueueCapacity = 8,
                RequestLogShutdownTimeoutSeconds = 5,
            },
        });
        using var worker = new RequestLogBufferWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<RequestLogBufferWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        Assert.True(worker.TryWrite(CreateLog("/api/write-failure")));

        await worker.FlushAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        RequestLogMetricsResponse metrics = worker.GetMetrics();
        Assert.Equal(1, metrics.EnqueuedTotal);
        Assert.Equal(0, metrics.PersistedTotal);
        Assert.Equal(1, metrics.DroppedTotal);
        Assert.Equal(0, metrics.RejectedTotal);
        Assert.Equal(1, metrics.LostAfterEnqueueTotal);
        Assert.Equal(1, metrics.ErrorsTotal);
        Assert.Equal(0, metrics.OutstandingTotal);
        Assert.True(metrics.LossDetected);
        Assert.NotNull(metrics.LastDroppedAt);
        Assert.NotNull(metrics.LastErrorAt);
    }

    private static RequestLog CreateLog(string path) => new()
    {
        Id = 0,
        Method = "GET",
        Path = path,
        StatusCode = 200,
        CreatedAt = DateTime.UtcNow,
    };

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

    private sealed class FailingRequestLogRepository : IRequestLogRepository
    {
        public Task<RequestLogListResponse> ListAsync(
            RequestLogQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddBatchAsync(
            IReadOnlyCollection<RequestLog> requestLogs,
            CancellationToken cancellationToken) => Task.FromException(
                new InvalidOperationException("request-log database unavailable"));

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
