using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Workers;

public sealed partial class RetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<SharpbillOptions> options,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    private readonly CancellationTokenSource _stopScheduling = new();
    private readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(
        options.Value.RequestPipeline.RetentionShutdownTimeoutSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(options.Value.Retention.WorkerIntervalSeconds);
        using var timer = new PeriodicTimer(interval);
        using var schedulingToken = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            _stopScheduling.Token);
        try
        {
            while (await timer.WaitForNextTickAsync(schedulingToken.Token).ConfigureAwait(false))
            {
                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    var retentionService = scope.ServiceProvider.GetRequiredService<IRetentionService>();
                    RetentionCycleResponse result = await retentionService.RunCycleAsync(stoppingToken)
                        .ConfigureAwait(false);
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        string failedCategories = string.Join(',', result.FailedCategories);
                        LogCycle(
                            logger,
                            result.AccountsAnonymized,
                            result.RequestLogsDeleted,
                            result.SecurityEventsDeleted,
                            failedCategories);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LogCycleFailure(logger, exception);
                }
            }
        }
        catch (OperationCanceledException) when (schedulingToken.IsCancellationRequested)
        {
            // Normal host shutdown, either while idle or after the active cycle completed.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopScheduling.Cancel();
        try
        {
            Task? execution = ExecuteTask;
            if (execution is not null)
            {
                using var gracefulStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gracefulStop.CancelAfter(_shutdownTimeout);
                try
                {
                    await execution.WaitAsync(gracefulStop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    LogShutdownTimeout(logger, _shutdownTimeout.TotalSeconds);
                }
            }
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _stopScheduling.Dispose();
        base.Dispose();
    }

    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Information,
        Message = "Retention cycle completed: accounts={Accounts}, request_logs={RequestLogs}, security_events={SecurityEvents}, failed_categories={FailedCategories}")]
    private static partial void LogCycle(
        ILogger logger,
        int accounts,
        int requestLogs,
        int securityEvents,
        string failedCategories);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Error, Message = "Retention cycle failed")]
    private static partial void LogCycleFailure(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Warning,
        Message = "Retention cycle exceeded the {TimeoutSeconds}-second shutdown grace; cancelling it")]
    private static partial void LogShutdownTimeout(ILogger logger, double timeoutSeconds);
}
