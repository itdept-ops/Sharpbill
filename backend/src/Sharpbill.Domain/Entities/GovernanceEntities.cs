using Sharpbill.Domain.Enums;

namespace Sharpbill.Domain.Entities;

public sealed record SiteSettings
{
    public int Id { get; init; } = 1;
    public SignupMode SignupMode { get; init; } = SignupMode.Open;
    public bool AllowGoogle { get; init; } = true;
    public bool AllowMicrosoft { get; init; } = true;
    public required int DefaultRoleId { get; init; }
    public bool CalmMode { get; init; }
    public bool RetentionHold { get; init; }
    public string? RetentionHoldReference { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record LegalAcceptance
{
    public required long Id { get; init; }
    public required int UserId { get; init; }
    public required string BundleVersion { get; init; }
    public required string TermsVersion { get; init; }
    public required string EulaVersion { get; init; }
    public required string AcceptableUseVersion { get; init; }
    public required string PrivacyVersion { get; init; }
    public required string TermsSha256 { get; init; }
    public required string EulaSha256 { get; init; }
    public required string AcceptableUseSha256 { get; init; }
    public required string PrivacySha256 { get; init; }
    public DateOnly BundleEffectiveDate { get; init; }
    public required string AcceptanceLabel { get; init; }
    public LegalAcceptanceAction TermsAction { get; init; }
    public LegalAcceptanceAction EulaAction { get; init; }
    public LegalAcceptanceAction AcceptableUseAction { get; init; }
    public LegalAcceptanceAction PrivacyAction { get; init; }
    public DateTime AcceptedAt { get; init; }
    public DateTime RetentionUntil { get; init; }
    public string? SourceIp { get; init; }
    public string? UserAgent { get; init; }
    public string? RequestId { get; init; }
    public DateTime? PersonalDataErasedAt { get; init; }
}

public sealed record SecurityEvent
{
    public required long Id { get; init; }
    public required string EventType { get; init; }
    public SecurityEventOutcome Outcome { get; init; }
    public SecurityEventSeverity Severity { get; init; }
    public string? RequestId { get; init; }
    public int? ActorUserId { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? SourceIp { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();
    public DateTime OccurredAt { get; init; }
    public DateTime RetentionUntil { get; init; }
}

public sealed record SecurityEventDelivery
{
    public required long EventId { get; init; }
    public EventDeliveryStatus Status { get; init; }
    public int Attempts { get; init; }
    public DateTime NextAttemptAt { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTime? LeaseExpiresAt { get; init; }
    public DateTime? LastAttemptAt { get; init; }
    public DateTime? DeliveredAt { get; init; }
    public string? LastError { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record RequestLog
{
    public required long Id { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int? UserId { get; init; }
    public string? IpAddress { get; init; }
    public int StatusCode { get; init; }
    public DateTime CreatedAt { get; init; }
}
