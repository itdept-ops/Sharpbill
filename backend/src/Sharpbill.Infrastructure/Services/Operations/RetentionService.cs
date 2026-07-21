using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Operations;

public sealed partial class RetentionService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<SharpbillOptions> options,
    ILogger<RetentionService> logger) : IRetentionService
{
    public async Task<RetentionCycleResponse> RunCycleAsync(CancellationToken cancellationToken)
    {
        RetentionOptions policy = options.Value.Retention;
        DateTime now = clock.UtcNow;
        CategoryResult nonces = await RunCategoryAsync(
            "nonces",
            policy.NonceBatchSize,
            policy.WorkerMaxBatchesPerCycle,
            governed: false,
            (provider, limit, token) => provider.GetRequiredService<INonceRepository>()
                .PruneExpiredAsync(now, limit, token),
            cancellationToken).ConfigureAwait(false);
        CategoryResult requestLogs = await RunCategoryAsync(
            "request_logs",
            policy.RequestLogBatchSize,
            policy.WorkerMaxBatchesPerCycle,
            governed: true,
            (provider, limit, token) => provider.GetRequiredService<IRequestLogRepository>()
                .PruneAsync(now.AddDays(-policy.RequestLogDays), limit, token),
            cancellationToken).ConfigureAwait(false);
        CategoryResult sessions = await RunCategoryAsync(
            "sessions",
            policy.SessionBatchSize,
            policy.WorkerMaxBatchesPerCycle,
            governed: true,
            (provider, limit, token) => provider.GetRequiredService<ISessionRepository>()
                .PruneAsync(now.AddDays(-policy.SessionDays), limit, token),
            cancellationToken).ConfigureAwait(false);
        CategoryResult preciseLocations = await RunCategoryAsync(
            "precise_locations",
            policy.PreciseLocationBatchSize,
            policy.WorkerMaxBatchesPerCycle,
            governed: true,
            (provider, limit, token) => provider.GetRequiredService<IUserRepository>()
                .ClearExpiredLocationsAsync(now, limit, token),
            cancellationToken).ConfigureAwait(false);
        CategoryResult accounts = await RunCategoryAsync(
            "accounts",
            policy.AccountBatchSize,
            policy.WorkerMaxBatchesPerCycle,
            governed: true,
            (provider, limit, token) => provider.GetRequiredService<IRetentionRepository>()
                .AnonymizeDueAccountsAsync(now, limit, token),
            cancellationToken).ConfigureAwait(false);
        CategoryResult securityEvents = await RunCategoryAsync(
            "security_events",
            policy.SecurityEventBatchSize,
            policy.WorkerMaxBatchesPerCycle,
            governed: true,
            (provider, limit, token) => provider.GetRequiredService<ISecurityEventRepository>()
                .PruneAsync(now, limit, token),
            cancellationToken).ConfigureAwait(false);
        CategoryResult legalAcceptances = await RunCategoryAsync(
            "legal_acceptances",
            policy.LegalAcceptanceBatchSize,
            policy.WorkerMaxBatchesPerCycle,
            governed: true,
            (provider, limit, token) => provider.GetRequiredService<ILegalAcceptanceRepository>()
                .PruneAsync(now.AddDays(-policy.LegalAcceptanceDays), limit, token),
            cancellationToken).ConfigureAwait(false);
        CategoryResult[] categories =
        [
            nonces,
            requestLogs,
            sessions,
            preciseLocations,
            accounts,
            securityEvents,
            legalAcceptances,
        ];

        return new RetentionCycleResponse
        {
            NoncesDeleted = nonces.Count,
            NonceBatches = nonces.Batches,
            RequestLogsDeleted = requestLogs.Count,
            RequestLogBatches = requestLogs.Batches,
            SessionsDeleted = sessions.Count,
            SessionBatches = sessions.Batches,
            PreciseLocationsCleared = preciseLocations.Count,
            PreciseLocationBatches = preciseLocations.Batches,
            AccountsAnonymized = accounts.Count,
            AccountBatches = accounts.Batches,
            SecurityEventsDeleted = securityEvents.Count,
            SecurityEventBatches = securityEvents.Batches,
            LegalAcceptancesDeleted = legalAcceptances.Count,
            LegalAcceptanceBatches = legalAcceptances.Batches,
            FailedCategories = categories
                .Where(static category => category.Failed)
                .Select(static category => category.Name)
                .ToArray(),
        };
    }

    private async Task<CategoryResult> RunCategoryAsync(
        string category,
        int batchSize,
        int maximumBatches,
        bool governed,
        Func<IServiceProvider, int, CancellationToken, Task<int>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IServiceProvider provider = scope.ServiceProvider;
            var unitOfWork = provider.GetRequiredService<IUnitOfWork>();
            var retention = provider.GetRequiredService<IRetentionRepository>();
            DrainResult result = await DrainAsync(
                unitOfWork,
                retention,
                batchSize,
                maximumBatches,
                (limit, token) => operation(provider, limit, token),
                governed,
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                LogCategoryFailure(logger, result.Failure, category);
            }

            return new CategoryResult(category, result.Count, result.Batches, result.Failure is not null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogCategoryFailure(logger, exception, category);
            return new CategoryResult(category, Count: 0, Batches: 0, Failed: true);
        }
    }

    private static async Task<DrainResult> DrainAsync(
        IUnitOfWork unitOfWork,
        IRetentionRepository retention,
        int batchSize,
        int maximumBatches,
        Func<int, CancellationToken, Task<int>> operation,
        bool governed,
        CancellationToken cancellationToken)
    {
        int total = 0;
        int batches = 0;
        for (int index = 0; index < maximumBatches; index++)
        {
            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int changed = governed &&
                    await retention.IsHoldActiveAsync(true, cancellationToken).ConfigureAwait(false)
                    ? 0
                    : await operation(batchSize, cancellationToken).ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                total += changed;
                batches++;
                if (changed < batchSize)
                {
                    break;
                }
            }
            catch (Exception exception)
            {
                try
                {
                    await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the category's initiating failure; the category scope is disposed
                    // before the next category starts with a fresh connection and transaction.
                }

                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new DrainResult(total, batches, exception);
            }
        }

        return new DrainResult(total, batches, Failure: null);
    }

    private sealed record CategoryResult(string Name, int Count, int Batches, bool Failed);

    private sealed record DrainResult(int Count, int Batches, Exception? Failure);

    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Error,
        Message = "Retention category {Category} failed; continuing remaining categories in fresh scopes")]
    private static partial void LogCategoryFailure(ILogger logger, Exception exception, string category);
}
