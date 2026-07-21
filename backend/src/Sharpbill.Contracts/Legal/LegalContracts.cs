using System.Text.Json.Serialization;
using Sharpbill.Contracts.Common;

namespace Sharpbill.Contracts.Legal;

public sealed record LegalDocumentResponse
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }
    [JsonPropertyName("title")]
    public required string Title { get; init; }
    [JsonPropertyName("version")]
    public required string Version { get; init; }
    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
    [JsonPropertyName("url")]
    public required string Url { get; init; }
    [JsonPropertyName("acceptance")]
    public LegalAcceptanceContract Acceptance { get; init; }
}

public sealed record LegalManifestResponse
{
    [JsonPropertyName("bundle_version")]
    public required string BundleVersion { get; init; }
    [JsonPropertyName("effective_date")]
    public DateOnly EffectiveDate { get; init; }
    [JsonPropertyName("required_at_login")]
    public bool RequiredAtLogin { get; init; }
    [JsonPropertyName("acceptance_label")]
    public required string AcceptanceLabel { get; init; }
    [JsonPropertyName("precise_location_retention_hours")]
    public int PreciseLocationRetentionHours { get; init; }
    [JsonPropertyName("legal_acceptance_retention_days")]
    public int LegalAcceptanceRetentionDays { get; init; }
    [JsonPropertyName("documents")]
    public IReadOnlyList<LegalDocumentResponse> Documents { get; init; } = [];
}
