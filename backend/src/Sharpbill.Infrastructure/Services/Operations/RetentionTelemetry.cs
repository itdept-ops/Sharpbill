using System.Diagnostics.Metrics;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Privacy;

namespace Sharpbill.Infrastructure.Services.Operations;

public sealed class RetentionTelemetry : IDisposable
{
    public const string MeterName = "Sharpbill.Retention";

    private readonly object _gate = new();
    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _cycles;
    private readonly Counter<long> _changedItems;
    private readonly Counter<long> _categoryFailures;
    private RetentionMetricsResponse _metrics = new()
    {
        Categories = EmptyCategories(),
    };
    private int _activeCycles;

    public RetentionTelemetry()
    {
        _cycles = _meter.CreateCounter<long>(
            "sharpbill.retention.cycles",
            unit: "{cycle}",
            description: "Retention cycle terminal outcomes, including controlled cancellation.");
        _changedItems = _meter.CreateCounter<long>(
            "sharpbill.retention.items.changed",
            unit: "{item}",
            description: "Rows deleted, anonymized, or cleared by retention category.");
        _categoryFailures = _meter.CreateCounter<long>(
            "sharpbill.retention.category.failures",
            unit: "{failure}",
            description: "Retention category failures.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.retention.cycle.in_progress",
            () => GetMetrics().CycleInProgress ? 1 : 0,
            unit: "{cycle}",
            description: "Whether any retention cycle is currently active in this process.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.retention.consecutive_failed_cycles",
            () => GetMetrics().ConsecutiveFailedCycles,
            unit: "{cycle}",
            description: "Consecutive completed retention cycles with one or more failures.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.retention.hold.active",
            ObserveHold,
            unit: "{state}",
            description: "Whether the governed-data retention hold is active.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.retention.last_success",
            ObserveLastSuccess,
            unit: "s",
            description: "Unix timestamp of the last fully successful retention cycle.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.retention.backlog.due",
            ObserveBacklog,
            unit: "{item}",
            description: "Eligible retention backlog by category.");
        _ = _meter.CreateObservableGauge(
            "sharpbill.retention.backlog.oldest_age",
            ObserveOldestAge,
            unit: "s",
            description: "Age of the oldest eligible retention item by category.");
    }

    public RetentionMetricsResponse GetMetrics()
    {
        lock (_gate)
        {
            return _metrics;
        }
    }

    public void CycleStarted(DateTime startedAt)
    {
        lock (_gate)
        {
            _activeCycles++;
            _metrics = _metrics with
            {
                CycleInProgress = true,
                LastCycleStartedAt = Normalize(startedAt),
            };
        }
    }

    public void CycleCompleted(
        DateTime completedAt,
        RetentionCycleResponse result,
        RetentionBacklogSnapshot? backlog)
    {
        ArgumentNullException.ThrowIfNull(result);
        DateTime normalizedCompletedAt = Normalize(completedAt);
        HashSet<string> failedCategories = result.FailedCategories.ToHashSet(StringComparer.Ordinal);
        bool failed = failedCategories.Count > 0;
        RetentionMetricsResponse updated;
        lock (_gate)
        {
            _activeCycles = Math.Max(0, _activeCycles - 1);
            IReadOnlyList<RetentionCategoryMetricsResponse> categories = MergeCategories(
                _metrics.Categories,
                CycleCategories(result, failedCategories),
                backlog);
            DateTime? oldestEligibleAt = backlog is null
                ? _metrics.OldestEligibleAt
                : categories
                    .Where(static category => category.DueCount > 0)
                    .Select(static category => category.OldestEligibleAt)
                    .Where(static value => value.HasValue)
                    .Min();
            updated = _metrics with
            {
                CycleInProgress = _activeCycles > 0,
                CyclesTotal = _metrics.CyclesTotal + 1,
                FailedCyclesTotal = _metrics.FailedCyclesTotal + (failed ? 1 : 0),
                ConsecutiveFailedCycles = failed ? _metrics.ConsecutiveFailedCycles + 1 : 0,
                LastCycleCompletedAt = normalizedCompletedAt,
                LastSuccessfulCycleAt = failed
                    ? _metrics.LastSuccessfulCycleAt
                    : normalizedCompletedAt,
                LastFailedCycleAt = failed ? normalizedCompletedAt : _metrics.LastFailedCycleAt,
                LastFailureCategories = failed
                    ? failedCategories.Order().ToArray()
                    : _metrics.LastFailureCategories,
                RetentionHold = backlog?.RetentionHold ?? _metrics.RetentionHold,
                BacklogCapturedAt = backlog?.CapturedAt ?? _metrics.BacklogCapturedAt,
                TotalDueCount = backlog is null
                    ? _metrics.TotalDueCount
                    : categories.Sum(static category => category.DueCount ?? 0),
                OldestEligibleAt = oldestEligibleAt,
                OldestEligibleAgeSeconds = backlog is null
                    ? _metrics.OldestEligibleAgeSeconds
                    : AgeSeconds(backlog.CapturedAt, oldestEligibleAt),
                Categories = categories,
            };
            _metrics = updated;
        }

        string outcome = failed ? "failure" : "success";
        _cycles.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        foreach (RetentionCategoryMetricsResponse category in updated.Categories)
        {
            if (category.LastCycleChanged > 0)
            {
                _changedItems.Add(
                    category.LastCycleChanged,
                    new KeyValuePair<string, object?>("category", category.Category));
            }

            if (category.LastCycleFailed)
            {
                _categoryFailures.Add(
                    1,
                    new KeyValuePair<string, object?>("category", category.Category));
            }
        }

        foreach (string failedCategory in failedCategories.Where(static category =>
                     !KnownCategories.Contains(category, StringComparer.Ordinal)))
        {
            _categoryFailures.Add(
                1,
                new KeyValuePair<string, object?>("category", failedCategory));
        }
    }

    public void CycleFailed(DateTime failedAt, string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        lock (_gate)
        {
            _activeCycles = Math.Max(0, _activeCycles - 1);
            DateTime normalizedFailedAt = Normalize(failedAt);
            _metrics = _metrics with
            {
                CycleInProgress = _activeCycles > 0,
                CyclesTotal = _metrics.CyclesTotal + 1,
                FailedCyclesTotal = _metrics.FailedCyclesTotal + 1,
                ConsecutiveFailedCycles = _metrics.ConsecutiveFailedCycles + 1,
                LastCycleCompletedAt = normalizedFailedAt,
                LastFailedCycleAt = normalizedFailedAt,
                LastFailureCategories = [category],
            };
        }

        _cycles.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
        _categoryFailures.Add(1, new KeyValuePair<string, object?>("category", category));
    }

    public void CycleCancelled()
    {
        lock (_gate)
        {
            _activeCycles = Math.Max(0, _activeCycles - 1);
            _metrics = _metrics with { CycleInProgress = _activeCycles > 0 };
        }

        _cycles.Add(1, new KeyValuePair<string, object?>("outcome", "cancelled"));
    }

    public void Dispose()
    {
        _meter.Dispose();
        GC.SuppressFinalize(this);
    }

    private IEnumerable<Measurement<long>> ObserveBacklog()
    {
        RetentionMetricsResponse metrics = GetMetrics();
        foreach (RetentionCategoryMetricsResponse category in metrics.Categories)
        {
            if (category.DueCount is long dueCount)
            {
                yield return new Measurement<long>(
                    dueCount,
                    new KeyValuePair<string, object?>("category", category.Category),
                    new KeyValuePair<string, object?>("hold", metrics.RetentionHold));
            }
        }
    }

    private IEnumerable<Measurement<int>> ObserveHold()
    {
        if (GetMetrics().RetentionHold is bool retentionHold)
        {
            yield return new Measurement<int>(retentionHold ? 1 : 0);
        }
    }

    private IEnumerable<Measurement<long>> ObserveLastSuccess()
    {
        if (GetMetrics().LastSuccessfulCycleAt is DateTime lastSuccessfulCycleAt)
        {
            yield return new Measurement<long>(
                new DateTimeOffset(Normalize(lastSuccessfulCycleAt)).ToUnixTimeSeconds());
        }
    }

    private IEnumerable<Measurement<long>> ObserveOldestAge()
    {
        foreach (RetentionCategoryMetricsResponse category in GetMetrics().Categories)
        {
            if (category.OldestEligibleAgeSeconds is long ageSeconds)
            {
                yield return new Measurement<long>(
                    ageSeconds,
                    new KeyValuePair<string, object?>("category", category.Category));
            }
        }
    }

    private static RetentionCategoryMetricsResponse[] MergeCategories(
        IReadOnlyList<RetentionCategoryMetricsResponse> previous,
        IReadOnlyList<CycleCategory> cycle,
        RetentionBacklogSnapshot? backlog)
    {
        Dictionary<string, RetentionCategoryMetricsResponse> previousByName = previous
            .ToDictionary(static category => category.Category, StringComparer.Ordinal);
        Dictionary<string, RetentionBacklogCategory>? backlogByName = backlog?.Categories
            .ToDictionary(static category => category.Category, StringComparer.Ordinal);
        return cycle.Select(category =>
        {
            _ = previousByName.TryGetValue(category.Category, out RetentionCategoryMetricsResponse? prior);
            RetentionBacklogCategory? backlogCategory = null;
            _ = backlogByName?.TryGetValue(category.Category, out backlogCategory);
            DateTime? oldestEligibleAt = backlogCategory?.OldestEligibleAt ?? prior?.OldestEligibleAt;
            return new RetentionCategoryMetricsResponse
            {
                Category = category.Category,
                GovernedByHold = category.GovernedByHold,
                LastCycleChanged = category.Changed,
                LastCycleBatches = category.Batches,
                LastCycleFailed = category.Failed,
                DueCount = backlogCategory?.DueCount ?? prior?.DueCount,
                OldestEligibleAt = oldestEligibleAt,
                OldestEligibleAgeSeconds = backlog is null
                    ? prior?.OldestEligibleAgeSeconds
                    : AgeSeconds(backlog.CapturedAt, oldestEligibleAt),
            };
        }).ToArray();
    }

    private static IReadOnlyList<CycleCategory> CycleCategories(
        RetentionCycleResponse result,
        HashSet<string> failedCategories) =>
        [
            new("nonces", false, result.NoncesDeleted, result.NonceBatches,
                failedCategories.Contains("nonces")),
            new("request_logs", true, result.RequestLogsDeleted, result.RequestLogBatches,
                failedCategories.Contains("request_logs")),
            new("sessions", true, result.SessionsDeleted, result.SessionBatches,
                failedCategories.Contains("sessions")),
            new("precise_locations", true, result.PreciseLocationsCleared,
                result.PreciseLocationBatches, failedCategories.Contains("precise_locations")),
            new("accounts", true, result.AccountsAnonymized, result.AccountBatches,
                failedCategories.Contains("accounts")),
            new("security_events", true, result.SecurityEventsDeleted,
                result.SecurityEventBatches, failedCategories.Contains("security_events")),
            new("legal_acceptances", true, result.LegalAcceptancesDeleted,
                result.LegalAcceptanceBatches, failedCategories.Contains("legal_acceptances")),
        ];

    private static RetentionCategoryMetricsResponse[] EmptyCategories() =>
        KnownCategories.Select(category => new RetentionCategoryMetricsResponse
        {
            Category = category,
            GovernedByHold = !string.Equals(category, "nonces", StringComparison.Ordinal),
        }).ToArray();

    private static long? AgeSeconds(DateTime capturedAt, DateTime? oldestEligibleAt) =>
        oldestEligibleAt is null
            ? null
            : Math.Max(0, (long)(Normalize(capturedAt) - Normalize(oldestEligibleAt.Value)).TotalSeconds);

    private static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static readonly string[] KnownCategories =
    [
        "nonces",
        "request_logs",
        "sessions",
        "precise_locations",
        "accounts",
        "security_events",
        "legal_acceptances",
    ];

    private sealed record CycleCategory(
        string Category,
        bool GovernedByHold,
        int Changed,
        int Batches,
        bool Failed);
}
