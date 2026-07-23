using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed class AuthSessionOperationsService : IAuthSessionOperationsService
{
    private readonly ISessionRepository _sessionRepository;
    private readonly ISecurityEventRepository _securityEventRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly SharpbillOptions _options;
    private readonly MySqlTransientRetryExecutor _retryExecutor;

    public AuthSessionOperationsService(
        ISessionRepository sessionRepository,
        ISecurityEventRepository securityEventRepository,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOptions<SharpbillOptions> options,
        MySqlTransientRetryExecutor? retryExecutor = null)
    {
        _sessionRepository = sessionRepository ??
            throw new ArgumentNullException(nameof(sessionRepository));
        _securityEventRepository = securityEventRepository ??
            throw new ArgumentNullException(nameof(securityEventRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _retryExecutor = retryExecutor ?? MySqlTransientRetryExecutor.Default;
    }

    public async Task LogoutAsync(
        RequestContext context,
        CancellationToken cancellationToken)
    {
        if (context.SessionJti is not { } jti)
        {
            return;
        }

        await _retryExecutor.ExecuteAsync(
            "auth.logout",
            async _ =>
            {
                await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    UserSession? session = await _sessionRepository.FindByJtiAsync(
                        jti,
                        true,
                        cancellationToken).ConfigureAwait(false);
                    if (context.SessionUserId.HasValue &&
                        session?.UserId != context.SessionUserId.Value)
                    {
                        session = null;
                    }

                    int? auditUserId = session?.UserId ?? context.SessionUserId;
                    if (!auditUserId.HasValue)
                    {
                        await _unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    DateTime now = _clock.UtcNow;
                    bool revoked = session?.RevokedAt is null && session is not null;
                    if (revoked)
                    {
                        await _sessionRepository.RevokeAsync(
                            session!.Id,
                            now,
                            cancellationToken).ConfigureAwait(false);
                    }

                    var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["session_revoked"] = revoked,
                    };
                    SecurityEvent securityEvent = IdentitySecurityEventFactory.Create(
                        "auth.logout",
                        SecurityEventOutcome.Success,
                        SecurityEventSeverity.Info,
                        context,
                        now,
                        _options.Retention.SecurityEventDays,
                        auditUserId.Value,
                        "user",
                        auditUserId.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        metadata);
                    await _securityEventRepository.AddWithPendingDeliveryAsync(
                        securityEvent,
                        cancellationToken).ConfigureAwait(false);
                    await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await _unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}
