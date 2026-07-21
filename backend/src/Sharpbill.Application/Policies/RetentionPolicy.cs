using Sharpbill.Application.Common;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Application.Policies;

public sealed record RetentionPolicyOptions
{
    public int PreciseLocationHours { get; init; } = 24;
    public int PendingAccountsDays { get; init; } = 30;
    public int SessionsAfterExpiryOrRevocationDays { get; init; } = 30;
    public int RequestActivityDays { get; init; } = 90;
    public int ErasureGraceDays { get; init; } = 30;
    public int DisabledAccountsDays { get; init; } = 365;
    public int SecurityEventsDays { get; init; } = 400;
    public int LegalAcceptancesDays { get; init; } = 2_555;
}

public sealed class RetentionPolicyValidator : IValidator<RetentionPolicyOptions>
{
    public ValidationResult Validate(RetentionPolicyOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        AddRange(errors, value.PreciseLocationHours, 1, 720, "precise_location_hours");
        AddRange(errors, value.PendingAccountsDays, 1, 365, "pending_accounts_days");
        AddRange(
            errors,
            value.SessionsAfterExpiryOrRevocationDays,
            1,
            365,
            "sessions_after_expiry_or_revocation_days");
        AddRange(errors, value.RequestActivityDays, 1, 365, "request_activity_days");
        AddRange(errors, value.ErasureGraceDays, 1, 90, "erasure_grace_days");
        AddRange(errors, value.DisabledAccountsDays, 30, 2_555, "disabled_accounts_days");
        AddRange(errors, value.SecurityEventsDays, 30, 2_555, "security_events_days");
        AddRange(errors, value.LegalAcceptancesDays, 1, 3_650, "legal_acceptances_days");
        return errors.Count == 0 ? ValidationResult.Success : ValidationResult.Failure(errors);
    }

    private static void AddRange(
        List<ValidationFailure> errors,
        int value,
        int minimum,
        int maximum,
        string field)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add(new(
                field,
                "OUT_OF_RANGE",
                $"{field} must be between {minimum} and {maximum}"));
        }
    }
}

public static class RetentionPolicy
{
    public static RetentionPolicyResponse ToContract(RetentionPolicyOptions options) => new()
    {
        PreciseLocationHours = options.PreciseLocationHours,
        PendingAccountsDays = options.PendingAccountsDays,
        SessionsAfterExpiryOrRevocationDays = options.SessionsAfterExpiryOrRevocationDays,
        RequestActivityDays = options.RequestActivityDays,
        ErasureGraceDays = options.ErasureGraceDays,
        DisabledAccountsDays = options.DisabledAccountsDays,
        SecurityEventsDays = options.SecurityEventsDays,
        LegalAcceptancesDays = options.LegalAcceptancesDays,
        GeneratedExportsRetained = false,
    };

    public static DateTime PreciseLocationDeadline(DateTime capturedAt, RetentionPolicyOptions options) =>
        capturedAt.AddHours(options.PreciseLocationHours);

    public static DateTime ErasureDeadline(DateTime requestedAt, RetentionPolicyOptions options) =>
        requestedAt.AddDays(options.ErasureGraceDays);

    public static void EnsureErasureAllowed(User user, bool retentionHold)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (retentionHold)
        {
            throw ApiException.Conflict(
                "RETENTION_HOLD_ACTIVE",
                "Data deletion is suspended by an active legal hold");
        }

        if (string.Equals(user.RoleName, SystemRoleNames.Administrator, StringComparison.Ordinal))
        {
            throw ApiException.Conflict(
                "ERASURE_NOT_ALLOWED",
                "Administrator accounts cannot be scheduled for erasure");
        }

        if (user.ErasedAt is not null)
        {
            throw ApiException.Conflict("ERASURE_NOT_ALLOWED", "The account is already erased");
        }
    }
}
