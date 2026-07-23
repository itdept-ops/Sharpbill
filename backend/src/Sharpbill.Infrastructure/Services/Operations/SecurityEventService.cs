using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Exports;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Operations;

public sealed partial class SecurityEventService(
    ISecurityEventRepository repository,
    IUserRepository users,
    IClock clock,
    IOptions<SharpbillOptions> options,
    IRequestContextAccessor requestContextAccessor,
    IValidator<SecurityEventQuery> queryValidator) : ISecurityEventService
{
    public Task<long> RecordAsync(SecurityEventWrite securityEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        if (!EventTypePattern().IsMatch(securityEvent.EventType))
        {
            throw new ArgumentException("Invalid security event type.", nameof(securityEvent));
        }

        if (securityEvent.TargetType is not null && !TargetTypePattern().IsMatch(securityEvent.TargetType))
        {
            throw new ArgumentException("Invalid security event target type.", nameof(securityEvent));
        }

        if (!Enum.TryParse(securityEvent.Outcome, true, out SecurityEventOutcome outcome) ||
            !Enum.TryParse(securityEvent.Severity, true, out SecurityEventSeverity severity))
        {
            throw new ArgumentException("Invalid security event outcome or severity.", nameof(securityEvent));
        }

        RequestContext context = requestContextAccessor.Current;
        var entity = new SecurityEvent
        {
            Id = 0,
            EventType = securityEvent.EventType,
            Outcome = outcome,
            Severity = severity,
            RequestId = Truncate(securityEvent.RequestId ?? context.RequestId, 64),
            ActorUserId = securityEvent.ActorUserId,
            TargetType = securityEvent.TargetType,
            TargetId = Truncate(securityEvent.TargetId, 128),
            SourceIp = Truncate(securityEvent.SourceIp ?? context.IpAddress, 45),
            Metadata = SecurityMetadataSanitizer.Sanitize(securityEvent.Metadata),
            OccurredAt = clock.UtcNow,
            RetentionUntil = clock.UtcNow.AddDays(options.Value.Retention.SecurityEventDays),
        };
        return repository.AddWithPendingDeliveryAsync(entity, cancellationToken);
    }

    public async Task<SecurityEventListResponse> ListAsync(
        SecurityEventQuery query,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        queryValidator.Validate(query).ThrowIfInvalid();
        await RequireViewerAsync(actorUserId, cancellationToken).ConfigureAwait(false);
        return await repository.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportDocument> ExportAsync(
        SecurityEventQuery query,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > DomainLimits.MaxExportRows)
        {
            ValidationResult.Failure(new ValidationFailure(
                "limit",
                "OUT_OF_RANGE",
                "limit must be between 1 and 10000")).ThrowIfInvalid();
        }

        queryValidator.Validate(query with { Limit = Math.Min(query.Limit, 500) }).ThrowIfInvalid();
        await RequireViewerAsync(actorUserId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SecurityEventResponse> rows = await repository.ListForExportAsync(
            query,
            Math.Clamp(query.Limit, 1, DomainLimits.MaxExportRows),
            cancellationToken).ConfigureAwait(false);

        IEnumerable<IReadOnlyList<string>> csvRows = BuildCsvRows(rows);
        CsvExportWriter.EnsureWithinLimit(
            csvRows,
            options.Value.RequestPipeline.ExportMaxBytes,
            cancellationToken);

        await RecordAsync(new SecurityEventWrite
        {
            EventType = "security_events.exported",
            Outcome = "success",
            ActorUserId = actorUserId,
            TargetType = "security_event_collection",
            Metadata = new Dictionary<string, object?>
            {
                ["exported_count"] = rows.Count,
                ["limit"] = query.Limit,
            },
        }, cancellationToken).ConfigureAwait(false);

        return new ExportDocument(
            "security-events.csv",
            "text/csv; charset=utf-8",
            (destination, writeCancellationToken) => CsvExportWriter.WriteAsync(
                destination,
                csvRows,
                writeCancellationToken));
    }

    private async Task RequireViewerAsync(int actorUserId, CancellationToken cancellationToken)
    {
        User? actor = await users.FindAsync(actorUserId, false, cancellationToken).ConfigureAwait(false);
        ServiceAuthorization.Require(actor, PermissionKeys.SecurityEventsView);
    }

    private static IEnumerable<IReadOnlyList<string>> BuildCsvRows(
        IReadOnlyList<SecurityEventResponse> rows)
    {
        yield return
        [
            "id", "occurred_at", "event_type", "outcome", "severity", "request_id",
            "actor_user_id", "target_type", "target_id", "source_ip", "metadata_json",
            "delivery_status", "delivery_attempts", "delivered_at", "retention_until",
        ];
        foreach (SecurityEventResponse row in rows)
        {
            yield return
            [
                row.Id.ToString(CultureInfo.InvariantCulture),
                row.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
                row.EventType,
                row.Outcome,
                row.Severity,
                row.RequestId ?? string.Empty,
                row.ActorUserId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.TargetType ?? string.Empty,
                row.TargetId ?? string.Empty,
                row.SourceIp ?? string.Empty,
                JsonSerializer.Serialize(row.Metadata),
                row.DeliveryStatus,
                row.DeliveryAttempts.ToString(CultureInfo.InvariantCulture),
                row.DeliveredAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.RetentionUntil.ToString("O", CultureInfo.InvariantCulture),
            ];
        }
    }

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, length)];

    public static IReadOnlyDictionary<string, object?> Summarize(IEnumerable<string> values)
    {
        string[] normalized = values.Select(static value => value[..Math.Min(value.Length, 100)])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalized))));
        return new Dictionary<string, object?>
        {
            ["count"] = normalized.Length,
            ["sha256"] = digest,
            ["sample"] = normalized.Take(8).ToArray(),
            ["sample_truncated"] = normalized.Length > 8,
        };
    }

    [GeneratedRegex("^[a-z][a-z0-9_.-]{2,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypePattern();

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetTypePattern();
}
