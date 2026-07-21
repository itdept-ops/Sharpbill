using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Operations;

public sealed record PresenceUserResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
    [JsonPropertyName("role")]
    public required string Role { get; init; }
    [JsonPropertyName("last_seen_at")]
    public DateTime? LastSeenAt { get; init; }
}

public sealed record PresenceResponse
{
    [JsonPropertyName("online")]
    public IReadOnlyList<PresenceUserResponse> Online { get; init; } = [];
    [JsonPropertyName("count")]
    public int Count { get; init; }
    [JsonPropertyName("window_seconds")]
    public int WindowSeconds { get; init; }
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
    [JsonPropertyName("roster_limit")]
    public int RosterLimit { get; init; }
}

public sealed record HeartbeatResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;
    [JsonPropertyName("user_id")]
    public int UserId { get; init; }
}

public sealed record PresenceSocketUser
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
    [JsonPropertyName("role")]
    public required string Role { get; init; }
}

public sealed record PresenceSocketMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "presence";
    [JsonPropertyName("count")]
    public int Count { get; init; }
    [JsonPropertyName("online")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PresenceSocketUser>? Online { get; init; }
}
