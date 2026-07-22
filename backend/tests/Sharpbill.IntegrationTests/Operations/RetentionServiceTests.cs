using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Services.Operations;
using Sharpbill.IntegrationTests.Business;

namespace Sharpbill.IntegrationTests.Operations;

public sealed class RetentionServiceTests
{
    [Fact]
    public async Task FailedCategoryDoesNotStarveLaterSecurityAndLegalCategoriesAsync()
    {
        var probe = new RetentionProbe();
        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => new ProbeUnitOfWork(probe));
        services.AddSingleton<IRetentionRepository>(new ProbeRetentionRepository());
        services.AddSingleton<INonceRepository>(new ProbeNonceRepository());
        services.AddSingleton<IRequestLogRepository>(new FailingRequestLogRepository());
        services.AddSingleton<ISessionRepository>(new ProbeSessionRepository());
        services.AddSingleton<IUserRepository>(new FakeUserRepository());
        services.AddSingleton<ISecurityEventRepository>(new ProbeSecurityEventRepository(probe));
        services.AddSingleton<ILegalAcceptanceRepository>(new ProbeLegalAcceptanceRepository(probe));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var service = new RetentionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeClock(),
            Options.Create(new SharpbillOptions()),
            NullLogger<RetentionService>.Instance);

        RetentionCycleResponse result = await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(["request_logs"], result.FailedCategories);
        Assert.Equal(1, result.SecurityEventsDeleted);
        Assert.Equal(1, result.LegalAcceptancesDeleted);
        Assert.Equal(1, probe.SecurityEventCalls);
        Assert.Equal(1, probe.LegalAcceptanceCalls);
        Assert.Equal(7, probe.UnitOfWorksCreated);
        Assert.Equal(6, probe.Commits);
        Assert.Equal(1, probe.Rollbacks);
    }

    private sealed class RetentionProbe
    {
        public int UnitOfWorksCreated;
        public int Commits;
        public int Rollbacks;
        public int SecurityEventCalls;
        public int LegalAcceptanceCalls;
    }

    private sealed class ProbeUnitOfWork : IUnitOfWork
    {
        private readonly RetentionProbe _probe;

        public ProbeUnitOfWork(RetentionProbe probe)
        {
            _probe = probe;
            Interlocked.Increment(ref _probe.UnitOfWorksCreated);
        }

        public Task BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _probe.Commits);
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _probe.Rollbacks);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProbeRetentionRepository : IRetentionRepository
    {
        public Task<bool> IsHoldActiveAsync(bool forUpdate, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<int> AnonymizeDueAccountsAsync(
            DateTime now,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class ProbeNonceRepository : INonceRepository
    {
        public Task<int> CountActiveAsync(DateTime now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(LoginNonce nonce, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryAddWithinCapacityAsync(
            LoginNonce nonce,
            DateTime now,
            int maximumOutstanding,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ConsumeAsync(
            string nonce,
            DateTime now,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PruneExpiredAsync(
            DateTime now,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class FailingRequestLogRepository : IRequestLogRepository
    {
        public Task<RequestLogListResponse> ListAsync(
            RequestLogQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AddBatchAsync(
            IReadOnlyCollection<RequestLog> requestLogs,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PruneAsync(
            DateTime cutoff,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromException<int>(new InvalidOperationException("request-log storage unavailable"));
    }

    private sealed class ProbeSessionRepository : ISessionRepository
    {
        public Task<UserSession?> FindByJtiAsync(
            Guid jti,
            bool forUpdate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserSession?> FindByJtiForAuthenticationAsync(
            Guid jti,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<UserSession?> FindAsync(
            int sessionId,
            bool forUpdate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<UserSession>> ListActiveAsync(
            int userId,
            DateTime now,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> CountActiveAsync(
            int userId,
            DateTime now,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> AddAsync(UserSession session, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TouchAsync(
            int sessionId,
            DateTime seenAt,
            DateTime staleBefore,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RevokeAsync(
            int sessionId,
            DateTime revokedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RevokeAllAsync(
            int userId,
            DateTime revokedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PruneAsync(
            DateTime cutoff,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class ProbeSecurityEventRepository(RetentionProbe probe) : ISecurityEventRepository
    {
        public Task<long> AddWithPendingDeliveryAsync(
            SecurityEvent securityEvent,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SecurityEventListResponse> ListAsync(
            SecurityEventQuery query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SecurityEventResponse>> ListForExportAsync(
            SecurityEventQuery query,
            int limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PruneAsync(DateTime cutoff, int limit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.SecurityEventCalls);
            return Task.FromResult(1);
        }
    }

    private sealed class ProbeLegalAcceptanceRepository(RetentionProbe probe) : ILegalAcceptanceRepository
    {
        public Task<long> AddAsync(
            LegalAcceptance acceptance,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<LegalAcceptance>> ListForUserAsync(
            int userId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> ErasePersonalDataAsync(
            int userId,
            DateTime erasedAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PruneAsync(DateTime cutoff, int limit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.LegalAcceptanceCalls);
            return Task.FromResult(1);
        }
    }
}
