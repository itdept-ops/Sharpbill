namespace Sharpbill.Application.Identity;

public sealed record AuthenticationPolicyOptions
{
    public bool IsLocal { get; init; }

    public string? GoogleClientId { get; init; }

    public string? MicrosoftClientId { get; init; }

    public bool DevelopmentAuthenticationEnabled { get; init; }

    public IReadOnlySet<string> GoogleAdminSubjects { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public string? MicrosoftAdminTenantId { get; init; }

    public IReadOnlySet<string> MicrosoftAdminObjectIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> DevelopmentAdminEmails { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
