using System.Globalization;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Legal;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.Legal;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class LegalService(
    ILegalAcceptanceRepository acceptanceRepository,
    ISecurityEventRepository securityEventRepository,
    IClock clock,
    IOptions<SharpbillOptions> options) : ILegalService
{
    private readonly SharpbillOptions _options = options.Value;

    public LegalManifestResponse GetManifest() => LegalAcceptancePolicy.CreateManifest(
        _options.Retention.PreciseLocationHours,
        _options.Retention.LegalAcceptanceDays);

    public void RequireCurrentAcceptance(bool accepted, string bundleVersion) =>
        LegalAcceptancePolicy.RequireCurrent(accepted, bundleVersion);

    /// <remarks>
    /// This method deliberately stages evidence in the caller's ambient unit of work. Session
    /// issuance owns the transaction so the legal snapshot, its outbox event, and the session row
    /// cannot be committed independently.
    /// </remarks>
    public async Task RecordAcceptanceAsync(
        int userId,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        DateTime acceptedAt = clock.UtcNow;
        LegalAcceptance evidence = LegalAcceptancePolicy.CreateEvidence(
            0,
            userId,
            acceptedAt,
            _options.Retention.LegalAcceptanceDays,
            context.IpAddress,
            context.UserAgent,
            context.RequestId);
        _ = await acceptanceRepository.AddAsync(evidence, cancellationToken).ConfigureAwait(false);

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["bundle_version"] = evidence.BundleVersion,
            ["terms_version"] = evidence.TermsVersion,
            ["eula_version"] = evidence.EulaVersion,
            ["acceptable_use_version"] = evidence.AcceptableUseVersion,
            ["privacy_version"] = evidence.PrivacyVersion,
            ["terms_sha256"] = evidence.TermsSha256,
            ["eula_sha256"] = evidence.EulaSha256,
            ["acceptable_use_sha256"] = evidence.AcceptableUseSha256,
            ["privacy_sha256"] = evidence.PrivacySha256,
            ["bundle_effective_date"] = evidence.BundleEffectiveDate.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            ["acceptance_label"] = evidence.AcceptanceLabel,
            ["terms_action"] = ActionName(evidence.TermsAction),
            ["eula_action"] = ActionName(evidence.EulaAction),
            ["acceptable_use_action"] = ActionName(evidence.AcceptableUseAction),
            ["privacy_action"] = ActionName(evidence.PrivacyAction),
        };
        SecurityEvent securityEvent = IdentitySecurityEventFactory.Create(
            "legal.accepted",
            SecurityEventOutcome.Success,
            SecurityEventSeverity.Info,
            context,
            acceptedAt,
            _options.Retention.SecurityEventDays,
            userId,
            "legal_bundle",
            LegalBundleV3.BundleVersion,
            metadata);
        _ = await securityEventRepository.AddWithPendingDeliveryAsync(
            securityEvent,
            cancellationToken).ConfigureAwait(false);
    }

    private static string ActionName(LegalAcceptanceAction action) => action switch
    {
        LegalAcceptanceAction.Agreement => "agreement",
        _ => "acknowledgement",
    };
}
