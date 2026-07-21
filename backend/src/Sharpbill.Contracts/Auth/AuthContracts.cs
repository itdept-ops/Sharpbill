using System.Text.Json.Serialization;
using Sharpbill.Contracts.Common;

namespace Sharpbill.Contracts.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TokenLoginRequest
{
    [JsonPropertyName("id_token")]
    public required string IdToken { get; init; }

    [JsonPropertyName("legal_accepted")]
    public required bool LegalAccepted { get; init; }

    [JsonPropertyName("legal_bundle_version")]
    public required string LegalBundleVersion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DevLoginRequest
{
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("legal_accepted")]
    public required bool LegalAccepted { get; init; }

    [JsonPropertyName("legal_bundle_version")]
    public required string LegalBundleVersion { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocationUpdateRequest
{
    [JsonPropertyName("latitude")]
    public required double Latitude { get; init; }

    [JsonPropertyName("longitude")]
    public required double Longitude { get; init; }

    [JsonPropertyName("accuracy")]
    public double? Accuracy { get; init; }
}

public sealed record AuthConfigResponse
{
    [JsonPropertyName("google")]
    public bool Google { get; init; }

    [JsonPropertyName("microsoft")]
    public bool Microsoft { get; init; }

    [JsonPropertyName("google_client_id")]
    public string? GoogleClientId { get; init; }

    [JsonPropertyName("microsoft_client_id")]
    public string? MicrosoftClientId { get; init; }

    [JsonPropertyName("dev")]
    public bool Dev { get; init; }

    [JsonPropertyName("calm")]
    public bool Calm { get; init; }
}

public sealed record NonceResponse
{
    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }
}

public sealed record SessionResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; init; }

    [JsonPropertyName("ip")]
    public string? Ip { get; init; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("last_seen_at")]
    public DateTime? LastSeenAt { get; init; }

    [JsonPropertyName("current")]
    public bool Current { get; init; }
}

public sealed record SessionToken
{
    public required string Value { get; init; }
    public required Guid Jti { get; init; }
    public int UserId { get; init; }
    public DateTime IssuedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}

public sealed record VerifiedIdentity
{
    public ProviderContract Provider { get; init; }
    public required string Subject { get; init; }
    public string Namespace { get; init; } = string.Empty;
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public string? TenantId { get; init; }
    public string? HostedDomain { get; init; }
}
