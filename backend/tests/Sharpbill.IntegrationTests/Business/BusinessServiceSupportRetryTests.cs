using MySqlConnector;
using Sharpbill.Infrastructure.Services.Business;

namespace Sharpbill.IntegrationTests.Business;

public sealed class BusinessServiceSupportRetryTests
{
    [Fact]
    public async Task RetryableFailureReplaysTheWholeTransactionAtMostThreeTimesAsync()
    {
        var unitOfWork = new FakeUnitOfWork();
        int operationInvocations = 0;

        int result = await BusinessServiceSupport.InTransactionAsync(
            unitOfWork,
            () =>
            {
                operationInvocations++;
                return operationInvocations < 3
                    ? Task.FromException<int>(new RetryableTestException())
                    : Task.FromResult(42);
            },
            static exception => exception is RetryableTestException,
            static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, operationInvocations);
        Assert.Equal(3, unitOfWork.Begins);
        Assert.Equal(2, unitOfWork.Rollbacks);
        Assert.Equal(1, unitOfWork.Commits);
    }

    [Fact]
    public async Task NonRetryableFailureIsRolledBackAndPropagatedImmediatelyAsync()
    {
        var unitOfWork = new FakeUnitOfWork();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BusinessServiceSupport.InTransactionAsync(
                unitOfWork,
                static () => Task.FromException<int>(new InvalidOperationException("permanent")),
                CancellationToken.None));

        Assert.Equal(1, unitOfWork.Begins);
        Assert.Equal(1, unitOfWork.Rollbacks);
        Assert.Equal(0, unitOfWork.Commits);
    }

    [Fact]
    public async Task RetryBudgetStopsAfterThreeFailedTransactionsAsync()
    {
        var unitOfWork = new FakeUnitOfWork();
        int operationInvocations = 0;

        await Assert.ThrowsAsync<RetryableTestException>(() =>
            BusinessServiceSupport.InTransactionAsync(
                unitOfWork,
                () =>
                {
                    operationInvocations++;
                    return Task.FromException<int>(new RetryableTestException());
                },
                static exception => exception is RetryableTestException,
                static (_, _) => Task.CompletedTask,
                CancellationToken.None));

        Assert.Equal(3, operationInvocations);
        Assert.Equal(3, unitOfWork.Begins);
        Assert.Equal(3, unitOfWork.Rollbacks);
        Assert.Equal(0, unitOfWork.Commits);
    }

    [Fact]
    public async Task CancellationStopsRetryBeforeASecondTransactionBeginsAsync()
    {
        var unitOfWork = new FakeUnitOfWork();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BusinessServiceSupport.InTransactionAsync(
                unitOfWork,
                static () => Task.FromException<int>(new RetryableTestException()),
                static exception => exception is RetryableTestException,
                (_, token) =>
                {
                    cancellation.Cancel();
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                },
                cancellation.Token));

        Assert.Equal(1, unitOfWork.Begins);
        Assert.Equal(1, unitOfWork.Rollbacks);
        Assert.Equal(0, unitOfWork.Commits);
    }

    [Theory]
    [InlineData(MySqlErrorCode.LockDeadlock, true)]
    [InlineData(MySqlErrorCode.LockWaitTimeout, true)]
    [InlineData(MySqlErrorCode.AccessDenied, false)]
    public void OnlyExpectedMySqlConcurrencyErrorsAreRetryable(
        MySqlErrorCode errorCode,
        bool expected) =>
        Assert.Equal(expected, BusinessServiceSupport.IsRetryableTransactionError(errorCode));

    private sealed class RetryableTestException : Exception;
}
