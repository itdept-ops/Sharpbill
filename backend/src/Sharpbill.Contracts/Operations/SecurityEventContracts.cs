using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Operations;

public sealed record SecurityEventResponse
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
    [JsonPropertyName("actor_user_id")]
    public int? ActorUserId { get; init; }
    [JsonPropertyName("target_type")]
    public string? TargetType { get; init; }
    [JsonPropertyName("target_id")]
    public string? TargetId { get; init; }
    [JsonPropertyName("source_ip")]
    public string? SourceIp { get; init; }
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, JsonElement> Metadata { get; init; } =
        new Dictionary<string, JsonElement>();
    [JsonPropertyName("occurred_at")]
    public DateTime OccurredAt { get; init; }
    [JsonPropertyName("retention_until")]
    public DateTime RetentionUntil { get; init; }
    [JsonPropertyName("delivery_status")]
    public required string DeliveryStatus { get; init; }
    [JsonPropertyName("delivery_attempts")]
    public int DeliveryAttempts { get; init; }
    [JsonPropertyName("delivered_at")]
    public DateTime? DeliveredAt { get; init; }
}

public sealed record SecurityEventListResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<SecurityEventResponse> Items { get; init; } = [];
    [JsonPropertyName("next_cursor")]
    public long? NextCursor { get; init; }
}

public sealed record SecurityEventQuery
{
    public int Limit { get; init; } = 100;
    public long? BeforeId { get; init; }
    public string? EventType { get; init; }
    public string? Outcome { get; init; }
    public string? Severity { get; init; }
    public int? ActorUserId { get; init; }
    public string? RequestId { get; init; }
}

public sealed record SecurityEventWrite
{
    public required string EventType { get; init; }
    public required string Outcome { get; init; }
    public string Severity { get; init; } = "info";
    public int? ActorUserId { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? RequestId { get; init; }
    public string? SourceIp { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();
}

public sealed record EventDeliveryEnvelope
{
    public long EventId { get; init; }
    public required string EventType { get; init; }
    public required string Outcome { get; init; }
    public required string Severity { get; init; }
    public string? RequestId { get; init; }
    public int? ActorUserId { get; init; }
    public string? TargetType { get; init; }
    public string? TargetId { get; init; }
    public string? SourceIp { get; init; }
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();
    public DateTime OccurredAt { get; init; }
}
