using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Common;

public sealed record ApiErrorDetail
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ValidationErrorContract>? Errors { get; init; }
}

public sealed record ApiErrorResponse
{
    [JsonPropertyName("detail")]
    public required ApiErrorDetail Detail { get; init; }
}

public sealed record ValidationErrorContract
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("loc")]
    public required IReadOnlyList<object> Location { get; init; }

    [JsonPropertyName("msg")]
    public required string Message { get; init; }
}

public sealed record OkResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;
}
