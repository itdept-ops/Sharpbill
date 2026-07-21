using Sharpbill.Domain.Enums;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.Domain.Entities;

public sealed record User
{
    public required int Id { get; init; }
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public string? Title { get; init; }
    public string? Department { get; init; }
    public string? Phone { get; init; }
    public string? Location { get; init; }
    public string? Timezone { get; init; }
    public string? Bio { get; init; }
    public required int RoleId { get; init; }
    public required string RoleName { get; init; }
    public bool IsActive { get; init; } = true;
    public bool IsApproved { get; init; } = true;
    public int AccessVersion { get; init; } = 1;
    public DateTime? LastLoginAt { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public DateTime? SessionValidAfter { get; init; }
    public DateTime? DeactivatedAt { get; init; }
    public DateTime? ErasureRequestedAt { get; init; }
    public DateTime? ErasureDueAt { get; init; }
    public DateTime? ErasedAt { get; init; }
    public double? LastLatitude { get; init; }
    public double? LastLongitude { get; init; }
    public double? LastLocationAccuracy { get; init; }
    public DateTime? LastLocationAt { get; init; }
    public DateTime? LocationRetentionUntil { get; init; }
    public string? AccentColor { get; init; }
    public UiPreferences? UiPreferences { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public IReadOnlyList<UserIdentity> Identities { get; init; } = [];
    public IReadOnlySet<string> RolePermissionKeys { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> DirectPermissionKeys { get; init; } = new HashSet<string>();

    public IReadOnlySet<string> EffectivePermissionKeys =>
        RolePermissionKeys.Concat(DirectPermissionKeys).ToHashSet(StringComparer.Ordinal);

    public UserStatus Status => !IsApproved
        ? UserStatus.Pending
        : IsActive ? UserStatus.Active : UserStatus.Disabled;
}

public sealed record UserIdentity
{
    public required int Id { get; init; }
    public required int UserId { get; init; }
    public required IdentityProvider Provider { get; init; }
    public string ProviderNamespace { get; init; } = string.Empty;
    public required string ProviderSubject { get; init; }
    public string? ProviderTenantId { get; init; }
    public string? ProviderHostedDomain { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record UserSession
{
    public required int Id { get; init; }
    public required int UserId { get; init; }
    public required Guid Jti { get; init; }
    public string? UserAgent { get; init; }
    public string? IpAddress { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}

public sealed record LoginNonce
{
    public required string Nonce { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}
