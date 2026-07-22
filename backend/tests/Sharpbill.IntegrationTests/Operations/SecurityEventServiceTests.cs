using System.Text;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Validation;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Services.Operations;
using Sharpbill.IntegrationTests.Business;

namespace Sharpbill.IntegrationTests.Operations;

public sealed class SecurityEventServiceTests
{
    [Fact]
    public async Task ExportStreamsCsvAndNeutralizesSpreadsheetFormulasAsync()
    {
        var repository = new ExportSecurityEventRepository
        {
            ExportRows =
            [
                new SecurityEventResponse
                {
                    Id = 42,
                    EventType = "users.updated",
                    Outcome = "success",
                    Severity = "info",
                    TargetType = "user",
                    TargetId = "=SUM(1,1)",
                    OccurredAt = BusinessTestData.Timestamp,
                    RetentionUntil = BusinessTestData.Timestamp.AddDays(400),
                    DeliveryStatus = "pending",
                },
            ],
        };
        var users = new FakeUserRepository();
        users.Items[1] = BusinessTestData.User(
            1,
            "admin",
            [Sharpbill.Domain.Constants.PermissionKeys.SecurityEventsView]);
        var service = new SecurityEventService(
            repository,
            users,
            new FakeClock(),
            BusinessTestData.WrappedOptions(),
            new FakeRequestContextAccessor(),
            new SecurityEventQueryValidator());

        Application.Common.ExportDocument export = await service.ExportAsync(
            new SecurityEventQuery { Limit = 1 },
            1,
            CancellationToken.None);
        await using var destination = new AsyncOnlyWriteStream();
        await export.WriteAsync(destination, CancellationToken.None);
        string csv = Encoding.UTF8.GetString(destination.Content);

        Assert.Equal("security-events.csv", export.FileName);
        Assert.Equal("text/csv; charset=utf-8", export.ContentType);
        Assert.Contains("id,occurred_at,event_type", csv, StringComparison.Ordinal);
        Assert.Contains("'=SUM(1,1)", csv, StringComparison.Ordinal);
        Assert.Equal("security_events.exported", Assert.Single(repository.Added).EventType);
    }

    private sealed class ExportSecurityEventRepository : ISecurityEventRepository
    {
        public IReadOnlyList<SecurityEventResponse> ExportRows { get; init; } = [];

        public List<SecurityEvent> Added { get; } = [];

        public Task<long> AddWithPendingDeliveryAsync(
            SecurityEvent securityEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Added.Add(securityEvent);
            return Task.FromResult((long)Added.Count);
        }

        public Task<SecurityEventListResponse> ListAsync(
            SecurityEventQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SecurityEventListResponse());

        public Task<IReadOnlyList<SecurityEventResponse>> ListForExportAsync(
            SecurityEventQuery query,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<SecurityEventResponse>>(
                ExportRows.Take(limit).ToArray());
        }

        public Task<int> PruneAsync(
            DateTime cutoff,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class AsyncOnlyWriteStream : Stream
    {
        private readonly MemoryStream _inner = new();

        public byte[] Content => _inner.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new InvalidOperationException("Synchronous flush is forbidden");

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("Synchronous write is forbidden");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

    }
}
