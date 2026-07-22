using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Operations;

public sealed record RequestLogResponse
{
    [JsonPropertyName("id")]
    public long Id { get; init; }
    [JsonPropertyName("method")]
    public required string Method { get; init; }
    [JsonPropertyName("path")]
    public required string Path { get; init; }
    [JsonPropertyName("user_id")]
    public int? UserId { get; init; }
    [JsonPropertyName("user_email")]
    public string? UserEmail { get; init; }
    [JsonPropertyName("ip")]
    public string? Ip { get; init; }
    [JsonPropertyName("status_code")]
    public int StatusCode { get; init; }
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }
}

public sealed record RequestLogListResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<RequestLogResponse> Items { get; init; } = [];
    [JsonPropertyName("total")]
    public int Total { get; init; }
    [JsonPropertyName("next_cursor")]
    public long? NextCursor { get; init; }
}

public sealed record RequestLogMetricsResponse
{
    [JsonPropertyName("enqueued_total")]
    public long EnqueuedTotal { get; init; }
    [JsonPropertyName("persisted_total")]
    public long PersistedTotal { get; init; }
    [JsonPropertyName("dropped_total")]
    public long DroppedTotal { get; init; }
    [JsonPropertyName("rejected_total")]
    public long RejectedTotal { get; init; }
    [JsonPropertyName("lost_after_enqueue_total")]
    public long LostAfterEnqueueTotal { get; init; }
    [JsonPropertyName("errors_total")]
    public long ErrorsTotal { get; init; }
    [JsonPropertyName("outstanding_total")]
    public long OutstandingTotal { get; init; }
    [JsonPropertyName("queue_depth")]
    public int QueueDepth { get; init; }
    [JsonPropertyName("queue_capacity")]
    public int QueueCapacity { get; init; }
    [JsonPropertyName("running")]
    public bool Running { get; init; }
    [JsonPropertyName("loss_detected")]
    public bool LossDetected { get; init; }
    [JsonPropertyName("last_enqueued_at")]
    public DateTime? LastEnqueuedAt { get; init; }
    [JsonPropertyName("last_persisted_at")]
    public DateTime? LastPersistedAt { get; init; }
    [JsonPropertyName("last_dropped_at")]
    public DateTime? LastDroppedAt { get; init; }
    [JsonPropertyName("last_error_at")]
    public DateTime? LastErrorAt { get; init; }
}

public sealed record RequestLogQuery
{
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
    public long? BeforeId { get; init; }
    public string? Search { get; init; }
    public string? Method { get; init; }
    public int? UserId { get; init; }
}
