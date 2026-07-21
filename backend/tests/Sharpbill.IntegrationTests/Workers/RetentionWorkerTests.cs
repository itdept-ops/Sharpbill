using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Workers;

namespace Sharpbill.IntegrationTests.Workers;

public sealed class RetentionWorkerTests
{
    [Fact]
    public async Task ShutdownAllowsConfiguredGraceThenCancelsActiveCycleAsync()
    {
        var retentionService = new BlockingRetentionService();
        var services = new ServiceCollection();
        services.AddScoped<IRetentionService>(_ => retentionService);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = Options.Create(new SharpbillOptions
        {
            Retention = new RetentionOptions
            {
                WorkerIntervalSeconds = 1,
            },
            RequestPipeline = new RequestPipelineOptions
            {
                RetentionShutdownTimeoutSeconds = 1,
            },
        });
        using var worker = new RetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<RetentionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await retentionService.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task stopping = worker.StopAsync(stopTimeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(150), stopTimeout.Token);

        Assert.False(stopping.IsCompleted);
        Assert.False(retentionService.Canceled.Task.IsCompleted);

        await stopping;
        await retentionService.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class BlockingRetentionService : IRetentionService
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RetentionCycleResponse> RunCycleAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }

            return new RetentionCycleResponse();
        }
    }
}
