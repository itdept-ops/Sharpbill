using Microsoft.Extensions.Logging.Abstractions;
using Sharpbill.Contracts.Operations;
using Sharpbill.Infrastructure.Database;
using Sharpbill.IntegrationTests.Business;

namespace Sharpbill.IntegrationTests.Database;

public sealed class MySqlTransientRetryExecutorTests
{
    [Fact]
    public async Task RetryableTransactionRecoversWithinBudgetAndEmitsTelemetryAsync()
    {
        using var telemetry = new DatabaseRetryTelemetry();
        var executor = new MySqlTransientRetryExecutor(
            static exception => exception is RetryableTestException,
            static (_, _) => Task.CompletedTask,
            NullLogger<MySqlTransientRetryExecutor>.Instance,
            telemetry);
        var unitOfWork = new FakeUnitOfWork();
        int invocations = 0;

        int result = await executor.ExecuteTransactionAsync(
            unitOfWork,
            "test.recovery",
            _ =>
            {
                invocations++;
                return invocations < 3
                    ? Task.FromException<int>(new RetryableTestException())
                    : Task.FromResult(42);
            },
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, invocations);
        Assert.Equal(3, unitOfWork.Begins);
        Assert.Equal(2, unitOfWork.Rollbacks);
        Assert.Equal(1, unitOfWork.Commits);
        Assert.Equal(2, telemetry.RetryAttempts);
        Assert.Equal(1, telemetry.RecoveredTransactions);
        Assert.Equal(0, telemetry.ExhaustedTransactions);
        DatabaseRetryMetricsResponse metrics = telemetry.GetMetrics();
        Assert.NotNull(metrics.LastRetryAt);
        Assert.NotNull(metrics.LastRecoveredAt);
        Assert.Null(metrics.LastExhaustedAt);
    }

    [Fact]
    public async Task RetryableTransactionExhaustionIsBoundedAndObservableAsync()
    {
        using var telemetry = new DatabaseRetryTelemetry();
        var executor = new MySqlTransientRetryExecutor(
            static exception => exception is RetryableTestException,
            static (_, _) => Task.CompletedTask,
            NullLogger<MySqlTransientRetryExecutor>.Instance,
            telemetry);
        var unitOfWork = new FakeUnitOfWork();

        await Assert.ThrowsAsync<RetryableTestException>(() =>
            executor.ExecuteTransactionAsync(
                unitOfWork,
                "test.exhaustion",
                static _ => Task.FromException(new RetryableTestException()),
                CancellationToken.None));

        Assert.Equal(MySqlTransientRetryExecutor.MaximumAttempts, unitOfWork.Begins);
        Assert.Equal(MySqlTransientRetryExecutor.MaximumAttempts, unitOfWork.Rollbacks);
        Assert.Equal(0, unitOfWork.Commits);
        Assert.Equal(MySqlTransientRetryExecutor.MaximumAttempts - 1, telemetry.RetryAttempts);
        Assert.Equal(0, telemetry.RecoveredTransactions);
        Assert.Equal(1, telemetry.ExhaustedTransactions);
        Assert.NotNull(telemetry.GetMetrics().LastExhaustedAt);
    }

    private sealed class RetryableTestException : Exception;
}
