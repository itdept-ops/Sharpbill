using Sharpbill.Application.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;

namespace Sharpbill.Application.Identity;

public static class IdentitySecurityEventFactory
{
    public static SecurityEvent Create(
        string eventType,
        SecurityEventOutcome outcome,
        SecurityEventSeverity severity,
        RequestContext? context,
        DateTime occurredAt,
        int retentionDays,
        int? actorUserId = null,
        string? targetType = null,
        string? targetId = null,
        IReadOnlyDictionary<string, object?>? metadata = null) =>
        new()
        {
            Id = 0,
            EventType = eventType,
            Outcome = outcome,
            Severity = severity,
            RequestId = Truncate(context?.RequestId, 64),
            ActorUserId = actorUserId,
            TargetType = Truncate(targetType, 64),
            TargetId = Truncate(targetId, 255),
            SourceIp = Truncate(context?.IpAddress, 45),
            Metadata = metadata ?? new Dictionary<string, object?>(),
            OccurredAt = occurredAt,
            RetentionUntil = occurredAt.AddDays(retentionDays),
        };

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, maximumLength)];
}
