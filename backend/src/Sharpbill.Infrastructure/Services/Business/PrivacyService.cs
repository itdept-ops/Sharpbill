using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Business;

public sealed class PrivacyService : IPrivacyService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _users;
    private readonly ISettingsRepository _settings;
    private readonly ISecurityEventService _securityEvents;
    private readonly IClock _clock;
    private readonly IRequestContextAccessor _requestContextAccessor;
    private readonly IValidator<RetentionHoldUpdateRequest> _holdValidator;
    private readonly RetentionPolicyOptions _retention;

    public PrivacyService(
        IUnitOfWork unitOfWork,
        IUserRepository users,
        ISettingsRepository settings,
        ISecurityEventService securityEvents,
        IClock clock,
        IRequestContextAccessor requestContextAccessor,
        IValidator<RetentionHoldUpdateRequest> holdValidator,
        IOptions<SharpbillOptions> options)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _securityEvents = securityEvents ?? throw new ArgumentNullException(nameof(securityEvents));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _requestContextAccessor = requestContextAccessor ??
            throw new ArgumentNullException(nameof(requestContextAccessor));
        _holdValidator = holdValidator ?? throw new ArgumentNullException(nameof(holdValidator));
        ArgumentNullException.ThrowIfNull(options);
        RetentionOptions configured = options.Value.Retention;
        _retention = new RetentionPolicyOptions
        {
            PreciseLocationHours = configured.PreciseLocationHours,
            PendingAccountsDays = configured.PendingAccountDays,
            SessionsAfterExpiryOrRevocationDays = configured.SessionDays,
            RequestActivityDays = configured.RequestLogDays,
            ErasureGraceDays = configured.AccountErasureGraceDays,
            DisabledAccountsDays = configured.DisabledAccountDays,
            SecurityEventsDays = configured.SecurityEventDays,
            LegalAcceptancesDays = configured.LegalAcceptanceDays,
        };
    }

    public async Task<PrivacyStatusResponse> GetAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        User user = await RequireActorAsync(userId, null, false, cancellationToken)
            .ConfigureAwait(false);
        SiteSettings settings = await RequireSettingsAsync(false, cancellationToken)
            .ConfigureAwait(false);
        return ToStatus(user, settings);
    }

    public async Task<PrivacyAdminStatusResponse> GetAdministrationAsync(
        int actorUserId,
        CancellationToken cancellationToken)
    {
        _ = await RequireActorAsync(
            actorUserId,
            PermissionKeys.PrivacyManage,
            false,
            cancellationToken).ConfigureAwait(false);
        SiteSettings settings = await RequireSettingsAsync(false, cancellationToken)
            .ConfigureAwait(false);
        return ToAdministrationStatus(settings);
    }

    public Task DeleteLocationAsync(int userId, CancellationToken cancellationToken) =>
        BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                SiteSettings settings = await RequireSettingsAsync(true, cancellationToken)
                    .ConfigureAwait(false);
                EnsureNoRetentionHold(settings);
                User user = await _users.FindAsync(userId, true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ApiException.Unauthorized(
                        "INVALID_SESSION",
                        "Session invalid or expired");
                if (!BusinessServiceSupport.IsAuthenticatable(user))
                {
                    throw ApiException.Unauthorized(
                        "INVALID_SESSION",
                        "Session invalid or expired");
                }

                bool changed = HasLocation(user);
                User cleared = user with
                {
                    Location = null,
                    Timezone = null,
                    LastLatitude = null,
                    LastLongitude = null,
                    LastLocationAccuracy = null,
                    LastLocationAt = null,
                    LocationRetentionUntil = null,
                    UpdatedAt = _clock.UtcNow,
                };
                await _users.UpdateAsync(cleared, cancellationToken).ConfigureAwait(false);
                await RecordEventAsync(
                    "privacy.location.cleared",
                    user.Id,
                    user.Id,
                    new Dictionary<string, object?> { ["changed"] = changed },
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public async Task<PrivacyStatusResponse> RequestErasureAsync(
        int actorUserId,
        int targetUserId,
        CancellationToken cancellationToken)
    {
        (User target, SiteSettings settings) result =
            await BusinessServiceSupport.InTransactionAsync(
                _unitOfWork,
                async () =>
                {
                    SiteSettings settings = await RequireSettingsAsync(true, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureNoRetentionHold(settings);
                    (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
                        actorUserId,
                        targetUserId,
                        cancellationToken).ConfigureAwait(false);
                    if (actorUserId != targetUserId)
                    {
                        RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.PrivacyManage);
                    }

                    RetentionPolicy.EnsureErasureAllowed(target, false);
                    DateTime now = _clock.UtcNow;
                    DateTime dueAt = RetentionPolicy.ErasureDeadline(now, _retention);
                    User scheduled = target with
                    {
                        ErasureRequestedAt = now,
                        ErasureDueAt = dueAt,
                        UpdatedAt = now,
                    };
                    await _users.UpdateAsync(scheduled, cancellationToken).ConfigureAwait(false);
                    await RecordEventAsync(
                        "privacy.erasure.requested",
                        actor.Id,
                        target.Id,
                        new Dictionary<string, object?>
                        {
                            ["due_at"] = BusinessServiceSupport.IsoTimestamp(dueAt),
                            ["requested_by"] = actorUserId == targetUserId
                                ? "self"
                                : "administrator",
                        },
                        cancellationToken).ConfigureAwait(false);
                    return (scheduled, settings);
                },
                cancellationToken).ConfigureAwait(false);
        return ToStatus(result.target, result.settings);
    }

    public async Task<PrivacyStatusResponse> CancelErasureAsync(
        int actorUserId,
        int targetUserId,
        CancellationToken cancellationToken)
    {
        User cancelled = await BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    targetUserId,
                    cancellationToken).ConfigureAwait(false);
                if (actorUserId != targetUserId)
                {
                    RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.PrivacyManage);
                }

                if (target.ErasedAt is not null)
                {
                    throw ApiException.Conflict(
                        "ERASURE_NOT_ALLOWED",
                        "an erased account cannot be restored");
                }

                bool changed = target.ErasureRequestedAt is not null || target.ErasureDueAt is not null;
                User updated = target with
                {
                    ErasureRequestedAt = null,
                    ErasureDueAt = null,
                    UpdatedAt = _clock.UtcNow,
                };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                var metadata = new Dictionary<string, object?> { ["changed"] = changed };
                if (actorUserId != targetUserId)
                {
                    metadata["cancelled_by"] = "administrator";
                }

                await RecordEventAsync(
                    "privacy.erasure.cancelled",
                    actor.Id,
                    target.Id,
                    metadata,
                    cancellationToken).ConfigureAwait(false);
                return updated;
            },
            cancellationToken).ConfigureAwait(false);
        SiteSettings settings = await RequireSettingsAsync(false, cancellationToken)
            .ConfigureAwait(false);
        return ToStatus(cancelled, settings);
    }

    public async Task<PrivacyAdminStatusResponse> UpdateHoldAsync(
        int actorUserId,
        RetentionHoldUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _holdValidator.Validate(request).ThrowIfInvalid();
        SiteSettings updated = await BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                SiteSettings current = await RequireSettingsAsync(true, cancellationToken)
                    .ConfigureAwait(false);
                User actor = await RequireActorAsync(
                    actorUserId,
                    PermissionKeys.PrivacyManage,
                    true,
                    cancellationToken).ConfigureAwait(false);
                string? reference = request.Enabled ? request.Reference?.Trim() : null;
                SiteSettings changed = current with
                {
                    RetentionHold = request.Enabled,
                    RetentionHoldReference = reference,
                    UpdatedAt = _clock.UtcNow,
                };
                await _settings.UpdateAsync(changed, cancellationToken).ConfigureAwait(false);
                await _securityEvents.RecordAsync(
                    BusinessServiceSupport.SecurityEvent(
                        _requestContextAccessor,
                        "privacy.retention_hold.changed",
                        actor.Id,
                        "site_settings",
                        changed.Id,
                        new Dictionary<string, object?>
                        {
                            ["before_enabled"] = current.RetentionHold,
                            ["after_enabled"] = changed.RetentionHold,
                            ["before_reference"] = current.RetentionHoldReference,
                            ["after_reference"] = changed.RetentionHoldReference,
                            ["reference_changed"] = !string.Equals(
                                current.RetentionHoldReference,
                                changed.RetentionHoldReference,
                                StringComparison.Ordinal),
                        },
                        "warning"),
                    cancellationToken).ConfigureAwait(false);
                return changed;
            },
            cancellationToken).ConfigureAwait(false);
        return ToAdministrationStatus(updated);
    }

    private async Task<User> RequireActorAsync(
        int actorUserId,
        string? permission,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        User? actor = await _users.FindAsync(actorUserId, forUpdate, cancellationToken)
            .ConfigureAwait(false);
        if (!BusinessServiceSupport.IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        if (permission is not null)
        {
            RbacHierarchyPolicy.RequirePermission(actor!, permission);
        }

        return actor!;
    }

    private async Task<(User Actor, User Target)> LoadActorAndTargetForUpdateAsync(
        int actorUserId,
        int targetUserId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == targetUserId)
        {
            User sameUser = await _users.FindAsync(actorUserId, true, cancellationToken)
                .ConfigureAwait(false)
                ?? throw ApiException.NotFound("User not found");
            if (!BusinessServiceSupport.IsAuthenticatable(sameUser))
            {
                throw ApiException.Forbidden(
                    "FORBIDDEN",
                    "Your account can no longer perform this action");
            }

            return (sameUser, sameUser);
        }

        int firstId = Math.Min(actorUserId, targetUserId);
        int secondId = Math.Max(actorUserId, targetUserId);
        User? first = await _users.FindAsync(firstId, true, cancellationToken)
            .ConfigureAwait(false);
        User? second = await _users.FindAsync(secondId, true, cancellationToken)
            .ConfigureAwait(false);
        User? actor = actorUserId == firstId ? first : second;
        User? target = targetUserId == firstId ? first : second;
        if (!BusinessServiceSupport.IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        return (actor!, target ?? throw ApiException.NotFound("User not found"));
    }

    private async Task<SiteSettings> RequireSettingsAsync(
        bool forUpdate,
        CancellationToken cancellationToken) =>
        await _settings.GetAsync(forUpdate, cancellationToken).ConfigureAwait(false)
            ?? throw BusinessServiceSupport.SettingsNotInitialized();

    private static void EnsureNoRetentionHold(SiteSettings settings)
    {
        if (settings.RetentionHold)
        {
            throw new ApiException(
                423,
                "RETENTION_HOLD",
                "Data deletion is suspended by an active retention hold");
        }
    }

    private Task<long> RecordEventAsync(
        string eventType,
        int actorUserId,
        int targetUserId,
        IReadOnlyDictionary<string, object?> metadata,
        CancellationToken cancellationToken) =>
        _securityEvents.RecordAsync(
            BusinessServiceSupport.SecurityEvent(
                _requestContextAccessor,
                eventType,
                actorUserId,
                "user",
                targetUserId,
                metadata),
            cancellationToken);

    private PrivacyStatusResponse ToStatus(User user, SiteSettings settings) => new()
    {
        Policy = RetentionPolicy.ToContract(_retention),
        RetentionHold = settings.RetentionHold,
        ErasureRequestedAt = user.ErasureRequestedAt,
        ErasureDueAt = user.ErasureDueAt,
    };

    private PrivacyAdminStatusResponse ToAdministrationStatus(SiteSettings settings) => new()
    {
        Policy = RetentionPolicy.ToContract(_retention),
        RetentionHold = settings.RetentionHold,
        RetentionHoldReference = settings.RetentionHoldReference,
    };

    private static bool HasLocation(User user) =>
        user.Location is not null ||
        user.Timezone is not null ||
        user.LastLatitude is not null ||
        user.LastLongitude is not null ||
        user.LastLocationAccuracy is not null ||
        user.LastLocationAt is not null ||
        user.LocationRetentionUntil is not null;
}
