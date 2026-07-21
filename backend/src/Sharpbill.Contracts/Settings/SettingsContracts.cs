using System.Text.Json.Serialization;
using Sharpbill.Contracts.Common;

namespace Sharpbill.Contracts.Settings;

public sealed record SiteSettingsResponse
{
    [JsonPropertyName("signup_mode")]
    public SignupModeContract SignupMode { get; init; }
    [JsonPropertyName("allow_google")]
    public bool AllowGoogle { get; init; }
    [JsonPropertyName("allow_microsoft")]
    public bool AllowMicrosoft { get; init; }
    [JsonPropertyName("default_role_id")]
    public int DefaultRoleId { get; init; }
    [JsonPropertyName("default_role_name")]
    public required string DefaultRoleName { get; init; }
    [JsonPropertyName("calm_mode")]
    public bool CalmMode { get; init; }
    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SiteSettingsUpdateRequest
{
    [JsonPropertyName("signup_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<SignupModeContract?> SignupMode { get; init; }
    [JsonPropertyName("allow_google")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<bool?> AllowGoogle { get; init; }
    [JsonPropertyName("allow_microsoft")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<bool?> AllowMicrosoft { get; init; }
    [JsonPropertyName("default_role_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<int?> DefaultRoleId { get; init; }
    [JsonPropertyName("calm_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<bool?> CalmMode { get; init; }
}
