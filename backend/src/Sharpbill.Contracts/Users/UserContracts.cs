using System.Text.Json.Serialization;
using Sharpbill.Contracts.Common;

namespace Sharpbill.Contracts.Users;

public sealed record IdentityResponse
{
    [JsonPropertyName("provider")]
    public ProviderContract Provider { get; init; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; init; }

    [JsonPropertyName("subject")]
    public required string Subject { get; init; }
}

public sealed record UserResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("email")]
    public required string Email { get; init; }
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }
    [JsonPropertyName("title")]
    public string? Title { get; init; }
    [JsonPropertyName("department")]
    public string? Department { get; init; }
    [JsonPropertyName("phone")]
    public string? Phone { get; init; }
    [JsonPropertyName("location")]
    public string? Location { get; init; }
    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }
    [JsonPropertyName("bio")]
    public string? Bio { get; init; }
    [JsonPropertyName("accent_color")]
    public string? AccentColor { get; init; }
    [JsonPropertyName("ui_prefs")]
    public UiPreferencesContract? UiPreferences { get; init; }
    [JsonPropertyName("role")]
    public required string Role { get; init; }
    [JsonPropertyName("role_id")]
    public int RoleId { get; init; }
    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = [];
    [JsonPropertyName("role_permissions")]
    public IReadOnlyList<string> RolePermissions { get; init; } = [];
    [JsonPropertyName("direct_permissions")]
    public IReadOnlyList<string> DirectPermissions { get; init; } = [];
    [JsonPropertyName("access_version")]
    public int AccessVersion { get; init; }
    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }
    [JsonPropertyName("is_approved")]
    public bool IsApproved { get; init; }
    [JsonPropertyName("status")]
    public UserStatusContract Status { get; init; }
    [JsonPropertyName("identities")]
    public IReadOnlyList<IdentityResponse> Identities { get; init; } = [];
    [JsonPropertyName("auth_providers")]
    public IReadOnlyList<ProviderContract> AuthProviders { get; init; } = [];
    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }
    [JsonPropertyName("last_login_at")]
    public DateTime? LastLoginAt { get; init; }
    [JsonPropertyName("last_seen_at")]
    public DateTime? LastSeenAt { get; init; }
    [JsonPropertyName("online")]
    public bool Online { get; init; }
    [JsonPropertyName("last_latitude")]
    public double? LastLatitude { get; init; }
    [JsonPropertyName("last_longitude")]
    public double? LastLongitude { get; init; }
    [JsonPropertyName("last_location_accuracy")]
    public double? LastLocationAccuracy { get; init; }
    [JsonPropertyName("last_location_at")]
    public DateTime? LastLocationAt { get; init; }
}

public sealed record UserListResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<UserResponse> Items { get; init; } = [];
    [JsonPropertyName("total")]
    public int Total { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RoleAssignRequest
{
    [JsonPropertyName("role_id")]
    public required int RoleId { get; init; }
    [JsonPropertyName("expected_version")]
    public int? ExpectedVersion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatusUpdateRequest
{
    [JsonPropertyName("is_active")]
    public required bool IsActive { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PermissionGrantRequest
{
    [JsonPropertyName("permission_keys")]
    public IReadOnlyList<string> PermissionKeys { get; init; } = [];
    [JsonPropertyName("expected_version")]
    public int? ExpectedVersion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BulkActionRequest
{
    [JsonPropertyName("ids")]
    public required IReadOnlyList<int> Ids { get; init; }
    [JsonPropertyName("action")]
    public required BulkUserActionContract Action { get; init; }
    [JsonPropertyName("role_id")]
    public int? RoleId { get; init; }
}

public sealed record BulkItemResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

public sealed record BulkActionResponse
{
    [JsonPropertyName("applied")]
    public int Applied { get; init; }
    [JsonPropertyName("results")]
    public IReadOnlyList<BulkItemResponse> Results { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProfileUpdateRequest
{
    [JsonPropertyName("display_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> DisplayName { get; init; }
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Title { get; init; }
    [JsonPropertyName("department")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Department { get; init; }
    [JsonPropertyName("phone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Phone { get; init; }
    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Location { get; init; }
    [JsonPropertyName("timezone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Timezone { get; init; }
    [JsonPropertyName("bio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Bio { get; init; }
    [JsonPropertyName("accent_color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> AccentColor { get; init; }
    [JsonPropertyName("ui_prefs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<UiPreferencesContract?> UiPreferences { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiPreferencesContract
{
    [JsonPropertyName("base_tone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> BaseTone { get; init; }
    [JsonPropertyName("background_depth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> BackgroundDepth { get; init; }
    [JsonPropertyName("border_glow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> BorderGlow { get; init; }
    [JsonPropertyName("glow_intensity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> GlowIntensity { get; init; }
    [JsonPropertyName("scanlines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Scanlines { get; init; }
    [JsonPropertyName("corner_radius")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> CornerRadius { get; init; }
    [JsonPropertyName("motion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Motion { get; init; }
    [JsonPropertyName("rain_density")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<double?> RainDensity { get; init; }
    [JsonPropertyName("rain_speed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> RainSpeed { get; init; }
    [JsonPropertyName("rain_glyphs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> RainGlyphs { get; init; }
    [JsonPropertyName("font_family")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> FontFamily { get; init; }
    [JsonPropertyName("text_scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> TextScale { get; init; }
    [JsonPropertyName("density")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> Density { get; init; }
    [JsonPropertyName("high_contrast_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<bool?> HighContrastText { get; init; }
    [JsonPropertyName("reduce_transparency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<bool?> ReduceTransparency { get; init; }
    [JsonPropertyName("focus_ring")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<string?> FocusRing { get; init; }
    [JsonPropertyName("zebra_rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<bool?> ZebraRows { get; init; }
    [JsonPropertyName("link_underlines")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<bool?> LinkUnderlines { get; init; }
    [JsonPropertyName("v")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PatchField<int?> Version { get; init; }
}

public sealed record UserQuery
{
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
    public string? Search { get; init; }
    public string? Status { get; init; }
    public int? RoleId { get; init; }
    public bool? Online { get; init; }
}
