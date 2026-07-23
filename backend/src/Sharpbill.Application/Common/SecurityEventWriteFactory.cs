using System.Globalization;
using Sharpbill.Contracts.Operations;

namespace Sharpbill.Application.Common;

public static class SecurityEventWriteFactory
{
    public static SecurityEventWrite Create(
        IRequestContextAccessor requestContextAccessor,
        string eventType,
        int actorUserId,
        string targetType,
        object? targetId,
        IReadOnlyDictionary<string, object?> metadata,
        string severity = "info")
    {
        ArgumentNullException.ThrowIfNull(requestContextAccessor);
        RequestContext context = requestContextAccessor.Current;
        return new SecurityEventWrite
        {
            EventType = eventType,
            Outcome = "success",
            Severity = severity,
            ActorUserId = actorUserId,
            TargetType = targetType,
            TargetId = targetId is null
                ? null
                : Convert.ToString(targetId, CultureInfo.InvariantCulture),
            RequestId = context.RequestId,
            SourceIp = context.IpAddress,
            Metadata = metadata,
        };
    }
}
