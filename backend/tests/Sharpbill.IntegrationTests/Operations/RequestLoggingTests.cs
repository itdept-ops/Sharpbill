using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Validation;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Services.Operations;
using Sharpbill.IntegrationTests.Business;

namespace Sharpbill.IntegrationTests.Operations;

public sealed class RequestLoggingTests
{
    [Fact]
    public async Task ListingReturnsPersistedRowsWhenVisibilityFlushIsUnavailableAsync()
    {
        var users = new FakeUserRepository();
        users.Items[7] = BusinessTestData.User(
            7,
            SystemRoleNames.Administrator,
            [PermissionKeys.LogsView]);
        var repository = new CapturingRepository();
        var service = new RequestLogService(
            repository,
            users,
            new UnavailableFlushBuffer(),
            new RequestLogQueryValidator(),
            NullLogger<RequestLogService>.Instance);

        RequestLogListResponse result = await service.ListAsync(
            new RequestLogQuery(),
            7,
            CancellationToken.None);

        Assert.Same(repository.Response, result);
        Assert.Equal(1, repository.ListCalls);
    }

    private sealed class UnavailableFlushBuffer : IRequestLogBuffer
    {
        public bool TryWrite(RequestLog requestLog) => true;

        public Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromException(new ChannelClosedException());

        public RequestLogMetricsResponse GetMetrics() => new();
    }

    private sealed class CapturingRepository : IRequestLogRepository
    {
        public RequestLogListResponse Response { get; } = new();
        public int ListCalls { get; private set; }

        public Task<RequestLogListResponse> ListAsync(
            RequestLogQuery query,
            CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(Response);
        }

        public Task AddBatchAsync(
            IReadOnlyCollection<RequestLog> requestLogs,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PruneAsync(
            DateTime cutoff,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
