using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class SessionService(
    ISessionRepository sessionRepository,
    IUserRepository userRepository,
    ISecurityEventRepository securityEventRepository,
    ILegalService legalService,
    IUnitOfWork unitOfWork,
    IClock clock,
    IRequestContextAccessor requestContextAccessor,
    SessionJwtIssuer jwtIssuer,
    IOptions<SharpbillOptions> options) : ISessionService
{
    private const int PresenceRefreshSeconds = 15;
    private const int CorruptSessionRecoveryThreshold = 500;
    private readonly SharpbillOptions _options = options.Value;

    public async Task<SessionToken> StartAsync(
        int userId,
        bool legalAccepted,
        string legalBundleVersion,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionToken token = await StageStartAsync(
                userId,
                legalAccepted,
                legalBundleVersion,
                context,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return token;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SessionToken> StageStartAsync(
        int userId,
        bool legalAccepted,
        string legalBundleVersion,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        legalService.RequireCurrentAcceptance(legalAccepted, legalBundleVersion);
        DateTime now = clock.UtcNow;
        User user = await userRepository.FindForAuthenticationAsync(
            userId,
            cancellationToken).ConfigureAwait(false)
            ?? throw ApiException.Forbidden("ACCOUNT_DISABLED", "This account is unavailable");
        RequireAuthenticatable(user);

        await legalService.RecordAcceptanceAsync(userId, context, cancellationToken).ConfigureAwait(false);
        await EnforceConcurrentSessionCapAsync(userId, now, cancellationToken).ConfigureAwait(false);

        Guid jti = Guid.NewGuid();
        SessionToken token = jwtIssuer.Issue(userId, jti, now);
        _ = await sessionRepository.AddAsync(
            new UserSession
            {
                Id = 0,
                UserId = userId,
                Jti = jti,
                UserAgent = Truncate(context.UserAgent, 400),
                IpAddress = Truncate(context.IpAddress, 45),
                CreatedAt = now,
                ExpiresAt = token.ExpiresAt,
            },
            cancellationToken).ConfigureAwait(false);
        return token;
    }

    public async Task<IReadOnlyList<SessionResponse>> ListAsync(
        int actorUserId,
        int targetUserId,
        bool includeDeviceDetails,
        Guid? currentJti,
        CancellationToken cancellationToken)
    {
        User? actor = await userRepository.FindAsync(
            actorUserId,
            false,
            cancellationToken).ConfigureAwait(false);
        if (!IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        User? target = actorUserId == targetUserId
            ? actor
            : await userRepository.FindAsync(targetUserId, false, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            throw ApiException.NotFound("User not found");
        }

        bool revealDeviceDetails = includeDeviceDetails &&
            (actorUserId == targetUserId ||
             actor!.EffectivePermissionKeys.Contains(PermissionKeys.UsersManage));
        IReadOnlyList<UserSession> sessions = await sessionRepository.ListActiveAsync(
            targetUserId,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return sessions
            .OrderByDescending(static session => session.CreatedAt)
            .ThenByDescending(static session => session.Id)
            .Select(session => new SessionResponse
            {
                Id = session.Id,
                UserAgent = revealDeviceDetails ? session.UserAgent : null,
                Ip = revealDeviceDetails ? session.IpAddress : null,
                CreatedAt = session.CreatedAt,
                LastSeenAt = session.LastSeenAt,
                Current = currentJti.HasValue && session.Jti == currentJti.Value,
            })
            .ToArray();
    }

    public async Task RevokeAsync(
        int actorUserId,
        int targetUserId,
        int sessionId,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (User actor, User target) = await LoadActorAndTargetForUpdateAsync(
                actorUserId,
                targetUserId,
                cancellationToken).ConfigureAwait(false);
            bool selfService = actorUserId == targetUserId;
            if (!selfService)
            {
                RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.PresenceKick);
                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
            }

            UserSession session = await sessionRepository.FindAsync(
                sessionId,
                true,
                cancellationToken).ConfigureAwait(false)
                ?? throw ApiException.NotFound("Session not found");
            if (session.UserId != targetUserId)
            {
                throw ApiException.NotFound("Session not found");
            }

            DateTime now = clock.UtcNow;
            bool wasActive = session.RevokedAt is null;
            if (wasActive)
            {
                await sessionRepository.RevokeAsync(session.Id, now, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyDictionary<string, object?> metadata = selfService
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scope"] = "self",
                }
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["scope"] = "single",
                    ["session_id"] = session.Id,
                };
            SecurityEvent securityEvent = IdentitySecurityEventFactory.Create(
                "session.revoked",
                SecurityEventOutcome.Success,
                selfService ? SecurityEventSeverity.Info : SecurityEventSeverity.Warning,
                requestContextAccessor.Current,
                now,
                _options.Retention.SecurityEventDays,
                actorUserId,
                selfService ? "user_session" : "user",
                (selfService ? session.Id : target.Id)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                metadata);
            _ = await securityEventRepository.AddWithPendingDeliveryAsync(
                securityEvent,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <remarks>Stages revocation in the caller's ambient unit of work.</remarks>
    public async Task RevokeAllAsync(
        int targetUserId,
        DateTime validAfter,
        CancellationToken cancellationToken) =>
        _ = await sessionRepository.RevokeAllAsync(
            targetUserId,
            validAfter,
            cancellationToken).ConfigureAwait(false);

    public async Task<SessionValidationResult> ValidateAsync(
        int userId,
        Guid jti,
        DateTime issuedAt,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTime now = clock.UtcNow;
            User? user = await userRepository.FindForAuthenticationAsync(
                userId,
                cancellationToken).ConfigureAwait(false);
            UserSession? session = await sessionRepository.FindByJtiAsync(
                jti,
                true,
                cancellationToken).ConfigureAwait(false);
            if (user is null || !IsAuthenticatable(user))
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return SessionValidationResult.Invalid(
                    "INVALID_SESSION",
                    "Session invalid or expired");
            }

            if (IsGloballyRevoked(user, issuedAt))
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return SessionValidationResult.Invalid(
                    "SESSION_REVOKED",
                    "Your session was ended by an administrator");
            }

            if (session is null || session.UserId != userId || session.RevokedAt is not null ||
                session.ExpiresAt <= now)
            {
                await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return SessionValidationResult.Invalid(
                    "SESSION_REVOKED",
                    "This session was signed out");
            }

            if (user.LastSeenAt is null ||
                now - user.LastSeenAt > TimeSpan.FromSeconds(PresenceRefreshSeconds))
            {
                user = user with { LastSeenAt = now, UpdatedAt = now };
                await userRepository.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
            }

            if (session.LastSeenAt is null ||
                now - session.LastSeenAt > TimeSpan.FromSeconds(PresenceRefreshSeconds))
            {
                await sessionRepository.TouchAsync(session.Id, now, cancellationToken).ConfigureAwait(false);
            }

            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return SessionValidationResult.Valid(IdentityUserMapper.ToResponse(user, online: true));
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnforceConcurrentSessionCapAsync(
        int userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        int activeCount = await sessionRepository.CountActiveAsync(
            userId,
            now,
            cancellationToken).ConfigureAwait(false);
        int maximum = _options.Session.MaxActiveSessionsPerUser;
        if (activeCount > maximum + CorruptSessionRecoveryThreshold)
        {
            _ = await sessionRepository.RevokeAllAsync(userId, now, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<UserSession> active = await sessionRepository.ListActiveAsync(
            userId,
            now,
            cancellationToken).ConfigureAwait(false);
        int revokeCount = active.Count - maximum + 1;
        foreach (UserSession session in active
            .OrderBy(static item => item.CreatedAt)
            .ThenBy(static item => item.Id)
            .Take(Math.Max(revokeCount, 0)))
        {
            await sessionRepository.RevokeAsync(session.Id, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RequireAuthenticatable(User user)
    {
        if (user.ErasedAt is not null)
        {
            throw ApiException.Forbidden("ACCOUNT_ERASED", "This account has been erased");
        }

        if (!user.IsApproved)
        {
            throw ApiException.Forbidden(
                "PENDING_APPROVAL",
                "Your account is awaiting administrator approval");
        }

        if (!user.IsActive)
        {
            throw ApiException.Forbidden("ACCOUNT_DISABLED", "This account has been deactivated");
        }
    }

    private static bool IsAuthenticatable(User? user) =>
        user is not null && user.ErasedAt is null && user.IsApproved && user.IsActive;

    private async Task<(User Actor, User Target)> LoadActorAndTargetForUpdateAsync(
        int actorUserId,
        int targetUserId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == targetUserId)
        {
            User? same = await userRepository.FindAsync(
                actorUserId,
                true,
                cancellationToken).ConfigureAwait(false);
            if (!IsAuthenticatable(same))
            {
                throw ApiException.Forbidden(
                    "FORBIDDEN",
                    "Your account can no longer perform this action");
            }

            return (same!, same!);
        }

        int firstId = Math.Min(actorUserId, targetUserId);
        int secondId = Math.Max(actorUserId, targetUserId);
        User? first = await userRepository.FindAsync(firstId, true, cancellationToken)
            .ConfigureAwait(false);
        User? second = await userRepository.FindAsync(secondId, true, cancellationToken)
            .ConfigureAwait(false);
        User? actor = actorUserId == firstId ? first : second;
        User? target = targetUserId == firstId ? first : second;
        if (!IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        return (actor!, target ?? throw ApiException.NotFound("User not found"));
    }

    private static bool IsGloballyRevoked(User user, DateTime issuedAt)
    {
        if (user.SessionValidAfter is not { } cutoff)
        {
            return false;
        }

        long issuedAtSeconds = new DateTimeOffset(DateTime.SpecifyKind(issuedAt, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        long cutoffSeconds = new DateTimeOffset(DateTime.SpecifyKind(cutoff, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        return issuedAtSeconds <= cutoffSeconds;
    }

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) ? null : value[..Math.Min(value.Length, maximumLength)];
}
