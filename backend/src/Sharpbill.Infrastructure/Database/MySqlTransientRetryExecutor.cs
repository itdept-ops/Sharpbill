using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;

namespace Sharpbill.Infrastructure.Database;

public sealed partial class MySqlTransientRetryExecutor : ITransactionExecutor
{
    internal const int MaximumAttempts = 3;
    private readonly Func<Exception, bool> _isRetryable;
    private readonly Func<int, CancellationToken, Task> _delayBeforeRetry;
    private readonly ILogger<MySqlTransientRetryExecutor> _logger;
    private readonly DatabaseRetryTelemetry _telemetry;

    public MySqlTransientRetryExecutor(
        ILogger<MySqlTransientRetryExecutor> logger,
        DatabaseRetryTelemetry telemetry)
        : this(IsRetryableException, DelayBeforeRetryAsync, logger, telemetry)
    {
    }

    internal MySqlTransientRetryExecutor(
        Func<Exception, bool> isRetryable,
        Func<int, CancellationToken, Task> delayBeforeRetry,
        ILogger<MySqlTransientRetryExecutor> logger,
        DatabaseRetryTelemetry telemetry)
    {
        _isRetryable = isRetryable ?? throw new ArgumentNullException(nameof(isRetryable));
        _delayBeforeRetry = delayBeforeRetry ??
            throw new ArgumentNullException(nameof(delayBeforeRetry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public static MySqlTransientRetryExecutor Default { get; } = new(
        IsRetryableException,
        DelayBeforeRetryAsync,
        NullLogger<MySqlTransientRetryExecutor>.Instance,
        DatabaseRetryTelemetry.Shared);

    public async Task<T> ExecuteAsync<T>(
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                T result = await operation(cancellationToken).ConfigureAwait(false);
                if (attempt > 1)
                {
                    _telemetry.RecordRecovered(operationName);
                    LogRecovered(_logger, operationName, attempt);
                }

                return result;
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested && _isRetryable(exception))
            {
                if (attempt == MaximumAttempts)
                {
                    _telemetry.RecordExhausted(operationName, exception);
                    LogExhausted(_logger, exception, operationName, attempt, ErrorCode(exception));
                    throw;
                }

                _telemetry.RecordRetry(operationName, exception);
                LogRetry(_logger, exception, operationName, attempt, ErrorCode(exception));
                await _delayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new UnreachableException();
    }

    public async Task ExecuteAsync(
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = await ExecuteAsync(
            operationName,
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<T> ExecuteTransactionAsync<T>(
        IUnitOfWork unitOfWork,
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync(
            operationName,
            async token =>
            {
                await unitOfWork.BeginAsync(token).ConfigureAwait(false);
                try
                {
                    T result = await operation(token).ConfigureAwait(false);
                    await unitOfWork.CommitAsync(token).ConfigureAwait(false);
                    return result;
                }
                catch (Exception exception)
                {
                    await RollbackPreservingOriginalErrorAsync(unitOfWork).ConfigureAwait(false);
                    ExceptionDispatchInfo.Capture(exception).Throw();
                    throw;
                }
            },
            cancellationToken);
    }

    public async Task ExecuteTransactionAsync(
        IUnitOfWork unitOfWork,
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = await ExecuteTransactionAsync(
            unitOfWork,
            operationName,
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    async Task<T> ITransactionExecutor.ExecuteTransactionAsync<T>(
        IUnitOfWork unitOfWork,
        string operationName,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteTransactionAsync(
                unitOfWork,
                operationName,
                operation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            throw new PersistenceOperationException(
                "The persistence operation failed.",
                exception);
        }
    }

    async Task ITransactionExecutor.ExecuteTransactionAsync(
        IUnitOfWork unitOfWork,
        string operationName,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteTransactionAsync(
                unitOfWork,
                operationName,
                operation,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbException exception)
        {
            throw new PersistenceOperationException(
                "The persistence operation failed.",
                exception);
        }
    }

    internal static bool IsRetryableError(MySqlErrorCode errorCode) =>
        errorCode is MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout;

    private static bool IsRetryableException(Exception exception) =>
        exception is MySqlException mysqlException && IsRetryableError(mysqlException.ErrorCode);

    private static string ErrorCode(Exception exception) => exception is MySqlException mysqlException
        ? ((int)mysqlException.ErrorCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : exception.GetType().Name;

    private static Task DelayBeforeRetryAsync(
        int failedAttempt,
        CancellationToken cancellationToken)
    {
        int exponentialMilliseconds = Math.Min(100, 25 * (1 << (failedAttempt - 1)));
        int jitterMilliseconds = Random.Shared.Next(0, 26);
        return Task.Delay(
            TimeSpan.FromMilliseconds(exponentialMilliseconds + jitterMilliseconds),
            cancellationToken);
    }

    private static async Task RollbackPreservingOriginalErrorAsync(IUnitOfWork unitOfWork)
    {
        try
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the initiating database error. DatabaseSession releases failed transaction
            // state in finally blocks so the next bounded attempt can start cleanly.
        }
    }

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Warning,
        Message = "Retrying MySQL operation {Operation} after attempt {Attempt}; error={ErrorCode}")]
    private static partial void LogRetry(
        ILogger logger,
        Exception exception,
        string operation,
        int attempt,
        string errorCode);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Information,
        Message = "MySQL operation {Operation} recovered on attempt {Attempt}")]
    private static partial void LogRecovered(ILogger logger, string operation, int attempt);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Error,
        Message = "MySQL operation {Operation} exhausted retries after attempt {Attempt}; error={ErrorCode}")]
    private static partial void LogExhausted(
        ILogger logger,
        Exception exception,
        string operation,
        int attempt,
        string errorCode);
}

public sealed class DatabaseRetryTelemetry : IDisposable
{
    public const string MeterName = "Sharpbill.Database";
    private readonly Counter<long> _exhausted;
    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _recovered;
    private readonly Counter<long> _retries;
    private long _exhaustedTotal;
    private long _lastExhaustedUnixMilliseconds;
    private long _lastRecoveredUnixMilliseconds;
    private long _lastRetryUnixMilliseconds;
    private long _recoveredTotal;
    private long _retryAttempts;

    public DatabaseRetryTelemetry()
    {
        _retries = _meter.CreateCounter<long>(
            "sharpbill.database.transaction.retry_attempts",
            unit: "{attempt}",
            description: "Retry attempts after a MySQL deadlock or lock-wait timeout.");
        _recovered = _meter.CreateCounter<long>(
            "sharpbill.database.transaction.recovered",
            unit: "{transaction}",
            description: "Transactions that succeeded after at least one bounded retry.");
        _exhausted = _meter.CreateCounter<long>(
            "sharpbill.database.transaction.retry_exhausted",
            unit: "{transaction}",
            description: "Transactions that exhausted the bounded lock-fault retry budget.");
    }

    public static DatabaseRetryTelemetry Shared { get; } = new();

    public long RetryAttempts => Interlocked.Read(ref _retryAttempts);

    public long RecoveredTransactions => Interlocked.Read(ref _recoveredTotal);

    public long ExhaustedTransactions => Interlocked.Read(ref _exhaustedTotal);

    public DatabaseRetryMetricsResponse GetMetrics() => new()
    {
        RetryAttempts = RetryAttempts,
        RecoveredTransactions = RecoveredTransactions,
        ExhaustedTransactions = ExhaustedTransactions,
        LastRetryAt = ReadTimestamp(ref _lastRetryUnixMilliseconds),
        LastRecoveredAt = ReadTimestamp(ref _lastRecoveredUnixMilliseconds),
        LastExhaustedAt = ReadTimestamp(ref _lastExhaustedUnixMilliseconds),
    };

    internal void RecordRetry(string operation, Exception exception)
    {
        _ = Interlocked.Increment(ref _retryAttempts);
        RecordNow(ref _lastRetryUnixMilliseconds);
        var tags = Tags(operation, exception);
        _retries.Add(1, tags);
    }

    internal void RecordRecovered(string operation)
    {
        _ = Interlocked.Increment(ref _recoveredTotal);
        RecordNow(ref _lastRecoveredUnixMilliseconds);
        _recovered.Add(1, new KeyValuePair<string, object?>("operation", operation));
    }

    internal void RecordExhausted(string operation, Exception exception)
    {
        _ = Interlocked.Increment(ref _exhaustedTotal);
        RecordNow(ref _lastExhaustedUnixMilliseconds);
        var tags = Tags(operation, exception);
        _exhausted.Add(1, tags);
    }

    public void Dispose()
    {
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TagList Tags(string operation, Exception exception)
    {
        var tags = new TagList { { "operation", operation } };
        if (exception is MySqlException mysqlException)
        {
            tags.Add(
                "mysql.error_code",
                (int)mysqlException.ErrorCode);
        }
        else
        {
            tags.Add("error.type", exception.GetType().Name);
        }

        return tags;
    }

    private static void RecordNow(ref long destination) =>
        Interlocked.Exchange(ref destination, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static DateTime? ReadTimestamp(ref long source)
    {
        long value = Interlocked.Read(ref source);
        return value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
    }
}
