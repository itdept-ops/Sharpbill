using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Services.Operations;

namespace Sharpbill.Workers;

public sealed partial class RequestLogBufferWorker : BackgroundService, IRequestLogBuffer
{
    private const int MaximumBatchSize = 100;
    private readonly Channel<QueueItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RequestLogBufferWorker> _logger;
    private readonly int _capacity;
    private readonly TimeSpan _shutdownTimeout;
    private long _accepted;
    private long _persisted;
    private long _dropped;
    private long _writeFailures;
    private int _queueDepth;
    private int _writerAlive;

    public RequestLogBufferWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<SharpbillOptions> options,
        ILogger<RequestLogBufferWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _capacity = options.Value.RequestPipeline.RequestLogQueueCapacity;
        _shutdownTimeout = TimeSpan.FromSeconds(
            options.Value.RequestPipeline.RequestLogShutdownTimeoutSeconds);
        _channel = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public bool TryWrite(RequestLog requestLog)
    {
        ArgumentNullException.ThrowIfNull(requestLog);
        Interlocked.Increment(ref _queueDepth);
        if (!_channel.Writer.TryWrite(new LogItem(requestLog)))
        {
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref _dropped);
            LogDropped(_logger, Volatile.Read(ref _queueDepth));
            return false;
        }

        Interlocked.Increment(ref _accepted);
        return true;
    }

    public async Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Interlocked.Increment(ref _queueDepth);
        try
        {
            await _channel.Writer.WriteAsync(new FlushItem(completion), timeoutSource.Token).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _queueDepth);
            throw;
        }

        await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
    }

    public RequestLogMetricsResponse GetMetrics() => new()
    {
        EnqueuedTotal = Interlocked.Read(ref _accepted),
        PersistedTotal = Interlocked.Read(ref _persisted),
        DroppedTotal = Interlocked.Read(ref _dropped),
        ErrorsTotal = Interlocked.Read(ref _writeFailures),
        QueueDepth = Volatile.Read(ref _queueDepth),
        QueueCapacity = _capacity,
        Running = Volatile.Read(ref _writerAlive) == 1,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Volatile.Write(ref _writerAlive, 1);
        try
        {
            var batch = new List<RequestLog>(MaximumBatchSize);
            await foreach (QueueItem item in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                switch (item)
                {
                    case LogItem log:
                        batch.Add(log.Value);
                        while (batch.Count < MaximumBatchSize && _channel.Reader.TryPeek(out QueueItem? next) &&
                               next is LogItem && _channel.Reader.TryRead(out QueueItem? read))
                        {
                            Interlocked.Decrement(ref _queueDepth);
                            batch.Add(((LogItem)read).Value);
                        }

                        await PersistAsync(batch, stoppingToken).ConfigureAwait(false);
                        batch.Clear();
                        break;
                    case FlushItem flush:
                        flush.Completion.TrySetResult();
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            Volatile.Write(ref _writerAlive, 0);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
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
                    LogDrainTimeout(_logger, Volatile.Read(ref _queueDepth), _shutdownTimeout.TotalSeconds);
                }
            }
        }
        finally
        {
            try
            {
                await base.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ExecuteTask?.IsCompleted == true)
                {
                    AccountForUnpersistedItems();
                }
            }
        }
    }

    private async Task PersistAsync(List<RequestLog> batch, CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRequestLogRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await repository.AddBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _persisted, batch.Count);
            }
            catch
            {
                await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Add(ref _dropped, batch.Count);
            LogCanceledBatch(_logger, batch.Count);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Increment(ref _writeFailures);
            Interlocked.Add(ref _dropped, batch.Count);
            LogWriteFailure(_logger, exception, batch.Count);
        }
    }

    private void AccountForUnpersistedItems()
    {
        int remainingLogs = 0;
        while (_channel.Reader.TryRead(out QueueItem? item))
        {
            Interlocked.Decrement(ref _queueDepth);
            switch (item)
            {
                case LogItem:
                    remainingLogs++;
                    break;
                case FlushItem flush:
                    flush.Completion.TrySetCanceled();
                    break;
            }
        }

        if (remainingLogs > 0)
        {
            Interlocked.Add(ref _dropped, remainingLogs);
            LogShutdownDrops(_logger, remainingLogs);
        }
    }

    private abstract record QueueItem;
    private sealed record LogItem(RequestLog Value) : QueueItem;
    private sealed record FlushItem(TaskCompletionSource Completion) : QueueItem;

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Warning,
        Message = "Request-log buffer is full; event dropped at queue depth {QueueDepth}")]
    private static partial void LogDropped(ILogger logger, int queueDepth);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "Request-log database write failed for a batch of {Count} records")]
    private static partial void LogWriteFailure(ILogger logger, Exception exception, int count);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Warning,
        Message = "Request-log buffer did not drain {QueueDepth} queued items within {TimeoutSeconds} seconds; cancelling persistence")]
    private static partial void LogDrainTimeout(ILogger logger, int queueDepth, double timeoutSeconds);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Request-log persistence was cancelled with {Count} records in flight during shutdown")]
    private static partial void LogCanceledBatch(ILogger logger, int count);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Error,
        Message = "Request-log shutdown dropped {Count} queued records after the bounded drain expired")]
    private static partial void LogShutdownDrops(ILogger logger, int count);
}
