using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Privacy;

public sealed record RetentionPolicyResponse
{
    [JsonPropertyName("precise_location_hours")]
    public int PreciseLocationHours { get; init; }
    [JsonPropertyName("pending_accounts_days")]
    public int PendingAccountsDays { get; init; }
    [JsonPropertyName("sessions_after_expiry_or_revocation_days")]
    public int SessionsAfterExpiryOrRevocationDays { get; init; }
    [JsonPropertyName("request_activity_days")]
    public int RequestActivityDays { get; init; }
    [JsonPropertyName("erasure_grace_days")]
    public int ErasureGraceDays { get; init; }
    [JsonPropertyName("disabled_accounts_days")]
    public int DisabledAccountsDays { get; init; }
    [JsonPropertyName("security_events_days")]
    public int SecurityEventsDays { get; init; }
    [JsonPropertyName("legal_acceptances_days")]
    public int LegalAcceptancesDays { get; init; }
    [JsonPropertyName("generated_exports_retained")]
    public bool GeneratedExportsRetained { get; init; }
}

public sealed record PrivacyStatusResponse
{
    [JsonPropertyName("policy")]
    public required RetentionPolicyResponse Policy { get; init; }
    [JsonPropertyName("retention_hold")]
    public bool RetentionHold { get; init; }
    [JsonPropertyName("erasure_requested_at")]
    public DateTime? ErasureRequestedAt { get; init; }
    [JsonPropertyName("erasure_due_at")]
    public DateTime? ErasureDueAt { get; init; }
}

public sealed record PrivacyAdminStatusResponse
{
    [JsonPropertyName("policy")]
    public required RetentionPolicyResponse Policy { get; init; }
    [JsonPropertyName("retention_hold")]
    public bool RetentionHold { get; init; }
    [JsonPropertyName("retention_hold_reference")]
    public string? RetentionHoldReference { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RetentionHoldUpdateRequest
{
    [JsonPropertyName("enabled")]
    public required bool Enabled { get; init; }
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }
}

public sealed record RetentionCycleResponse
{
    [JsonPropertyName("nonces_deleted")]
    public int NoncesDeleted { get; init; }
    [JsonPropertyName("nonce_batches")]
    public int NonceBatches { get; init; }
    [JsonPropertyName("request_logs_deleted")]
    public int RequestLogsDeleted { get; init; }
    [JsonPropertyName("request_log_batches")]
    public int RequestLogBatches { get; init; }
    [JsonPropertyName("sessions_deleted")]
    public int SessionsDeleted { get; init; }
    [JsonPropertyName("session_batches")]
    public int SessionBatches { get; init; }
    [JsonPropertyName("precise_locations_cleared")]
    public int PreciseLocationsCleared { get; init; }
    [JsonPropertyName("precise_location_batches")]
    public int PreciseLocationBatches { get; init; }
    [JsonPropertyName("accounts_anonymized")]
    public int AccountsAnonymized { get; init; }
    [JsonPropertyName("account_batches")]
    public int AccountBatches { get; init; }
    [JsonPropertyName("security_events_deleted")]
    public int SecurityEventsDeleted { get; init; }
    [JsonPropertyName("security_event_batches")]
    public int SecurityEventBatches { get; init; }
    [JsonPropertyName("legal_acceptances_deleted")]
    public int LegalAcceptancesDeleted { get; init; }
    [JsonPropertyName("legal_acceptance_batches")]
    public int LegalAcceptanceBatches { get; init; }
    [JsonPropertyName("failed_categories")]
    public IReadOnlyList<string> FailedCategories { get; init; } = [];
}
