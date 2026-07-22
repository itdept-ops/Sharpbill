using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;
using Sharpbill.Infrastructure.Services.Operations;

namespace Sharpbill.Workers;

public sealed partial class RequestLogBufferWorker : BackgroundService, IRequestLogBuffer
{
    public const string MeterName = "Sharpbill.RequestLogs";

    private const int MaximumBatchSize = 100;
    private readonly Channel<QueueItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RequestLogBufferWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _events;
    private readonly MySqlTransientRetryExecutor _retryExecutor;
    private readonly int _capacity;
    private readonly TimeSpan _shutdownTimeout;
    private long _accepted;
    private long _persisted;
    private long _dropped;
    private long _rejected;
    private long _lostAfterEnqueue;
    private long _writeFailures;
    private long _lastEnqueuedUnixMilliseconds;
    private long _lastPersistedUnixMilliseconds;
    private long _lastDroppedUnixMilliseconds;
    private long _lastErrorUnixMilliseconds;
    private int _queueDepth;
    private int _writerAlive;

    public RequestLogBufferWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<SharpbillOptions> options,
        TimeProvider timeProvider,
        ILogger<RequestLogBufferWorker> logger,
        MySqlTransientRetryExecutor? retryExecutor = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider;
        _retryExecutor = retryExecutor ?? MySqlTransientRetryExecutor.Default;
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
        _events = _meter.CreateCounter<long>(
            "sharpbill.request_logs.events",
            unit: "{event}",
            description: "Request-log buffer events by outcome.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.request_logs.queue.depth",
            () => Volatile.Read(ref _queueDepth),
            unit: "{item}",
            description: "Current request-log channel depth, including flush markers.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.request_logs.queue.capacity",
            () => _capacity,
            unit: "{item}",
            description: "Configured request-log channel capacity.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.request_logs.outstanding",
            OutstandingTotal,
            unit: "{event}",
            description: "Accepted request logs not yet persisted or declared lost.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.request_logs.writer.running",
            () => Volatile.Read(ref _writerAlive),
            unit: "{state}",
            description: "Whether the request-log persistence loop is running.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.request_logs.loss_detected",
            () => Interlocked.Read(ref _dropped) > 0 ? 1 : 0,
            unit: "{state}",
            description: "Whether this process has rejected or lost any request logs.");
    }

    public bool TryWrite(RequestLog requestLog)
    {
        ArgumentNullException.ThrowIfNull(requestLog);
        Interlocked.Increment(ref _queueDepth);
        if (!_channel.Writer.TryWrite(new LogItem(requestLog)))
        {
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _rejected);
            RecordNow(ref _lastDroppedUnixMilliseconds);
            RecordEvent("rejected");
            LogDropped(_logger, Volatile.Read(ref _queueDepth));
            return false;
        }

        Interlocked.Increment(ref _accepted);
        RecordNow(ref _lastEnqueuedUnixMilliseconds);
        RecordEvent("accepted");
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

    public RequestLogMetricsResponse GetMetrics()
    {
        long accepted = Interlocked.Read(ref _accepted);
        long persisted = Interlocked.Read(ref _persisted);
        long dropped = Interlocked.Read(ref _dropped);
        long lostAfterEnqueue = Interlocked.Read(ref _lostAfterEnqueue);
        return new RequestLogMetricsResponse
        {
            EnqueuedTotal = accepted,
            PersistedTotal = persisted,
            DroppedTotal = dropped,
            RejectedTotal = Interlocked.Read(ref _rejected),
            LostAfterEnqueueTotal = lostAfterEnqueue,
            ErrorsTotal = Interlocked.Read(ref _writeFailures),
            OutstandingTotal = Math.Max(0, accepted - persisted - lostAfterEnqueue),
            QueueDepth = Volatile.Read(ref _queueDepth),
            QueueCapacity = _capacity,
            Running = Volatile.Read(ref _writerAlive) == 1,
            LossDetected = dropped > 0,
            LastEnqueuedAt = ReadTimestamp(ref _lastEnqueuedUnixMilliseconds),
            LastPersistedAt = ReadTimestamp(ref _lastPersistedUnixMilliseconds),
            LastDroppedAt = ReadTimestamp(ref _lastDroppedUnixMilliseconds),
            LastErrorAt = ReadTimestamp(ref _lastErrorUnixMilliseconds),
        };
    }

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
            await _retryExecutor.ExecuteTransactionAsync(
                unitOfWork,
                "request_logs.persist_batch",
                token => repository.AddBatchAsync(batch, token),
                cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _persisted, batch.Count);
            RecordNow(ref _lastPersistedUnixMilliseconds);
            RecordEvent("persisted", batch.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RecordAcceptedLoss(batch.Count);
            LogCanceledBatch(_logger, batch.Count);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Increment(ref _writeFailures);
            RecordNow(ref _lastErrorUnixMilliseconds);
            RecordEvent("write_error");
            RecordAcceptedLoss(batch.Count);
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
            RecordAcceptedLoss(remainingLogs);
            LogShutdownDrops(_logger, remainingLogs);
        }
    }

    public override void Dispose()
    {
        _meter.Dispose();
        base.Dispose();
    }

    private void RecordAcceptedLoss(int count)
    {
        Interlocked.Add(ref _dropped, count);
        Interlocked.Add(ref _lostAfterEnqueue, count);
        RecordNow(ref _lastDroppedUnixMilliseconds);
        RecordEvent("lost_after_enqueue", count);
    }

    private long OutstandingTotal() => Math.Max(
        0,
        Interlocked.Read(ref _accepted)
        - Interlocked.Read(ref _persisted)
        - Interlocked.Read(ref _lostAfterEnqueue));

    private void RecordNow(ref long timestamp) => Interlocked.Exchange(
        ref timestamp,
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

    private void RecordEvent(string outcome, long count = 1) => _events.Add(
        count,
        new KeyValuePair<string, object?>("outcome", outcome));

    private static DateTime? ReadTimestamp(ref long timestamp)
    {
        long unixMilliseconds = Interlocked.Read(ref timestamp);
        return unixMilliseconds == 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime;
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
