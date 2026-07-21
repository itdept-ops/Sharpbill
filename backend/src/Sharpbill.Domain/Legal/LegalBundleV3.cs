using System.Collections.Immutable;
using Sharpbill.Domain.Enums;

namespace Sharpbill.Domain.Legal;

public sealed record LegalDocumentDefinition(
    string Key,
    string Title,
    string Version,
    string Sha256,
    string Url,
    LegalAcceptanceAction Acceptance);

/// <summary>The immutable legal-document authority accepted by Sharpbill logins.</summary>
public static class LegalBundleV3
{
    public const string BundleVersion = "2026-07-21-v3";
    public const string Schema = "sharpbill-legal-document/v1";
    public const string AcceptanceLabel =
        "I agree to the Terms of Service, EULA, and Acceptable Use Policy, and acknowledge " +
        "the Privacy Notice.";
    public const string TermsSha256 =
        "37cbe7a0ff06fb3ba8c5914ad33065fd125b9c94b7a46852a9f9ce77643d1891";
    public const string EulaSha256 =
        "2bb333bb3d3314edb2cf945c0bc34212cc27d0f7aff35c414b16e4ec60c7cad2";
    public const string AcceptableUseSha256 =
        "b7715b13d4063b6c092fb85b779ed0be07b9a89fab8df5bd64a0f1cd1b015663";
    public const string PrivacySha256 =
        "20f67642f41b1639ddae3eeb19ca32d0372568c8969aaebb5747265ec577d024";

    public static DateOnly EffectiveDate { get; } = new(2026, 7, 21);

    public static ImmutableArray<LegalDocumentDefinition> Documents { get; } =
    [
        new(
            "terms",
            "Terms of Service",
            BundleVersion,
            TermsSha256,
            "/legal/terms-of-service.html",
            LegalAcceptanceAction.Agreement),
        new(
            "eula",
            "End User License Agreement",
            BundleVersion,
            EulaSha256,
            "/legal/eula.html",
            LegalAcceptanceAction.Agreement),
        new(
            "acceptable_use",
            "Acceptable Use Policy",
            BundleVersion,
            AcceptableUseSha256,
            "/legal/acceptable-use-policy.html",
            LegalAcceptanceAction.Agreement),
        new(
            "privacy",
            "Privacy Notice",
            BundleVersion,
            PrivacySha256,
            "/legal/privacy-notice.html",
            LegalAcceptanceAction.Acknowledgement),
    ];
}
