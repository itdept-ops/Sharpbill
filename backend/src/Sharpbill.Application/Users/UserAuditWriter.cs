using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;

namespace Sharpbill.Application.Users;

public sealed class UserAuditWriter
{
    private readonly ISecurityEventService _securityEvents;
    private readonly IRequestContextAccessor _requestContextAccessor;

    public UserAuditWriter(
        ISecurityEventService securityEvents,
        IRequestContextAccessor requestContextAccessor)
    {
        _securityEvents = securityEvents ?? throw new ArgumentNullException(nameof(securityEvents));
        _requestContextAccessor = requestContextAccessor ??
            throw new ArgumentNullException(nameof(requestContextAccessor));
    }

    public Task<long> RecordAsync(
        string eventType,
        int actorUserId,
        string targetType,
        object? targetId,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken,
        string severity = "info") =>
        _securityEvents.RecordAsync(
            SecurityEventWriteFactory.Create(
                _requestContextAccessor,
                eventType,
                actorUserId,
                targetType,
                targetId,
                metadata,
                severity),
            cancellationToken);
}
