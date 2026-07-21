using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Health;

public sealed record LivenessResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "alive";
}

public sealed record ReadinessResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }
    [JsonPropertyName("database")]
    public required string Database { get; init; }
    [JsonPropertyName("schema")]
    public required string Schema { get; init; }
    [JsonPropertyName("identity_provider")]
    public required string IdentityProvider { get; init; }
    [JsonPropertyName("administration")]
    public required string Administration { get; init; }
    [JsonPropertyName("admission_policy")]
    public required string AdmissionPolicy { get; init; }
}
