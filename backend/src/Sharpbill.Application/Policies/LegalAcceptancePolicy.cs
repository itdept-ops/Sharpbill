using Sharpbill.Application.Common;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Legal;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.Legal;

namespace Sharpbill.Application.Policies;

public static class LegalAcceptancePolicy
{
    public static void RequireCurrent(bool accepted, string bundleVersion)
    {
        if (!accepted)
        {
            throw ApiException.PreconditionRequired(
                "LEGAL_ACCEPTANCE_REQUIRED",
                "You must agree to the current legal terms and acknowledge the Privacy Notice");
        }

        if (!string.Equals(bundleVersion, LegalBundleV3.BundleVersion, StringComparison.Ordinal))
        {
            throw ApiException.Conflict(
                "LEGAL_BUNDLE_STALE",
                "The legal terms changed; review and accept the current versions before signing in");
        }
    }

    public static LegalManifestResponse CreateManifest(
        int preciseLocationRetentionHours,
        int legalAcceptanceRetentionDays) =>
        new()
        {
            BundleVersion = LegalBundleV3.BundleVersion,
            EffectiveDate = LegalBundleV3.EffectiveDate,
            RequiredAtLogin = true,
            AcceptanceLabel = LegalBundleV3.AcceptanceLabel,
            PreciseLocationRetentionHours = preciseLocationRetentionHours,
            LegalAcceptanceRetentionDays = legalAcceptanceRetentionDays,
            Documents = LegalBundleV3.Documents.Select(static document => new LegalDocumentResponse
            {
                Key = document.Key,
                Title = document.Title,
                Version = document.Version,
                Sha256 = document.Sha256,
                Url = document.Url,
                Acceptance = document.Acceptance == LegalAcceptanceAction.Agreement
                    ? LegalAcceptanceContract.Agreement
                    : LegalAcceptanceContract.Acknowledgement,
            }).ToArray(),
        };

    public static LegalAcceptance CreateEvidence(
        long id,
        int userId,
        DateTime acceptedAt,
        int retentionDays,
        string? sourceIp,
        string? userAgent,
        string? requestId)
    {
        if (retentionDays is < 1 or > 3_650)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        return new LegalAcceptance
        {
            Id = id,
            UserId = userId,
            BundleVersion = LegalBundleV3.BundleVersion,
            TermsVersion = LegalBundleV3.BundleVersion,
            EulaVersion = LegalBundleV3.BundleVersion,
            AcceptableUseVersion = LegalBundleV3.BundleVersion,
            PrivacyVersion = LegalBundleV3.BundleVersion,
            TermsSha256 = LegalBundleV3.TermsSha256,
            EulaSha256 = LegalBundleV3.EulaSha256,
            AcceptableUseSha256 = LegalBundleV3.AcceptableUseSha256,
            PrivacySha256 = LegalBundleV3.PrivacySha256,
            BundleEffectiveDate = LegalBundleV3.EffectiveDate,
            AcceptanceLabel = LegalBundleV3.AcceptanceLabel,
            TermsAction = LegalAcceptanceAction.Agreement,
            EulaAction = LegalAcceptanceAction.Agreement,
            AcceptableUseAction = LegalAcceptanceAction.Agreement,
            PrivacyAction = LegalAcceptanceAction.Acknowledgement,
            AcceptedAt = acceptedAt,
            RetentionUntil = acceptedAt.AddDays(retentionDays),
            SourceIp = Truncate(sourceIp, 45),
            UserAgent = Truncate(userAgent, 400),
            RequestId = Truncate(requestId, 64),
        };
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, maxLength)];
}
