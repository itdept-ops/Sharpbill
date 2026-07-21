using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Dashboard;

public sealed record DashboardStats
{
    [JsonPropertyName("total_users")]
    public int TotalUsers { get; init; }
    [JsonPropertyName("active_users")]
    public int ActiveUsers { get; init; }
    [JsonPropertyName("online_users")]
    public int OnlineUsers { get; init; }
}

public sealed record DashboardResponse
{
    [JsonPropertyName("stats")]
    public required DashboardStats Stats { get; init; }
}

public sealed record NamedCount
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }
    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }
    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed record SignupCount
{
    [JsonPropertyName("date")]
    public required string Date { get; init; }
    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed record StatusCounts
{
    [JsonPropertyName("total")]
    public int Total { get; init; }
    [JsonPropertyName("active")]
    public int Active { get; init; }
    [JsonPropertyName("pending")]
    public int Pending { get; init; }
    [JsonPropertyName("disabled")]
    public int Disabled { get; init; }
    [JsonPropertyName("online")]
    public int Online { get; init; }
}

public sealed record AnalyticsResponse
{
    [JsonPropertyName("roles")]
    public IReadOnlyList<NamedCount> Roles { get; init; } = [];
    [JsonPropertyName("providers")]
    public IReadOnlyList<NamedCount> Providers { get; init; } = [];
    [JsonPropertyName("signups")]
    public IReadOnlyList<SignupCount> Signups { get; init; } = [];
    [JsonPropertyName("status")]
    public required StatusCounts Status { get; init; }
}
