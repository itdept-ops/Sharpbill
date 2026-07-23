using Sharpbill.Application.Common;
using Sharpbill.Application.Identity;
using Sharpbill.Domain.Enums;

namespace Sharpbill.Application.Tests;

public sealed class IdentitySecurityEventFactoryTests
{
    [Fact]
    public void FactoryBoundsEvidenceAndAppliesRetentionPolicy()
    {
        DateTime occurredAt = new(2026, 7, 22, 14, 0, 0, DateTimeKind.Utc);
        var metadata = new Dictionary<string, object?> { ["provider"] = "google" };
        var context = new RequestContext
        {
            RequestId = new string('r', 80),
            IpAddress = new string('1', 60),
        };

        var securityEvent = IdentitySecurityEventFactory.Create(
            "auth.login",
            SecurityEventOutcome.Denied,
            SecurityEventSeverity.Warning,
            context,
            occurredAt,
            retentionDays: 400,
            actorUserId: 7,
            targetType: new string('t', 80),
            targetId: new string('i', 300),
            metadata: metadata);

        Assert.Equal(64, securityEvent.RequestId?.Length);
        Assert.Equal(45, securityEvent.SourceIp?.Length);
        Assert.Equal(64, securityEvent.TargetType?.Length);
        Assert.Equal(255, securityEvent.TargetId?.Length);
        Assert.Equal(occurredAt.AddDays(400), securityEvent.RetentionUntil);
        Assert.Same(metadata, securityEvent.Metadata);
        Assert.Equal(SecurityEventOutcome.Denied, securityEvent.Outcome);
        Assert.Equal(SecurityEventSeverity.Warning, securityEvent.Severity);
    }

    [Fact]
    public void FactoryUsesEmptyMetadataWhenNoneIsProvided()
    {
        var securityEvent = IdentitySecurityEventFactory.Create(
            "auth.logout",
            SecurityEventOutcome.Success,
            SecurityEventSeverity.Info,
            context: null,
            occurredAt: DateTime.UnixEpoch,
            retentionDays: 1);

        Assert.Empty(securityEvent.Metadata);
        Assert.Null(securityEvent.RequestId);
        Assert.Null(securityEvent.SourceIp);
    }
}
