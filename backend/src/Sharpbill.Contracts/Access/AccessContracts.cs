using System.Text.Json.Serialization;
using Sharpbill.Contracts.Common;

namespace Sharpbill.Contracts.Access;

public sealed record PermissionResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("key")]
    public required string Key { get; init; }
    [JsonPropertyName("description")]
    public string? Description { get; init; }
    [JsonPropertyName("is_system")]
    public bool IsSystem { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PermissionCreateRequest
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record RoleResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("description")]
    public string? Description { get; init; }
    [JsonPropertyName("is_system")]
    public bool IsSystem { get; init; }
    [JsonPropertyName("permissions")]
    public IReadOnlyList<PermissionResponse> Permissions { get; init; } = [];
    [JsonPropertyName("user_count")]
    public int UserCount { get; init; }
    [JsonPropertyName("version")]
    public int Version { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleCreateRequest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("description")]
    public string? Description { get; init; }
    [JsonPropertyName("permission_keys")]
    public IReadOnlyList<string> PermissionKeys { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleUpdateRequest
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Name { get; init; }
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Description { get; init; }
    [JsonPropertyName("permission_keys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<IReadOnlyList<string>?> PermissionKeys { get; init; }
    [JsonPropertyName("expected_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<int?> ExpectedVersion { get; init; }
}
