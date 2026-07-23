using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Identity;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed partial class AuthenticationAuditService
{
    private readonly ISecurityEventRepository _securityEventRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ILogger<AuthService> _logger;
    private readonly SharpbillOptions _options;
    private readonly MySqlTransientRetryExecutor _retryExecutor;

    public AuthenticationAuditService(
        ISecurityEventRepository securityEventRepository,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOptions<SharpbillOptions> options,
        ILogger<AuthService> logger,
        MySqlTransientRetryExecutor? retryExecutor = null)
    {
        _securityEventRepository = securityEventRepository ??
            throw new ArgumentNullException(nameof(securityEventRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryExecutor = retryExecutor ?? MySqlTransientRetryExecutor.Default;
    }

    public async Task AddLoginSuccessEventAsync(
        int userId,
        string provider,
        RequestContext context,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["provider"] = provider,
        };
        SecurityEvent securityEvent = IdentitySecurityEventFactory.Create(
            "auth.login",
            SecurityEventOutcome.Success,
            SecurityEventSeverity.Info,
            context,
            occurredAt,
            _options.Retention.SecurityEventDays,
            userId,
            "user",
            userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            metadata);
        _ = await _securityEventRepository.AddWithPendingDeliveryAsync(
            securityEvent,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task AuditLoginFailureAsync(
        string provider,
        string reason,
        SecurityEventOutcome outcome,
        RequestContext context)
    {
        // Authentication evidence is independent of the client connection. A disconnected
        // caller must not cancel the failure audit, but the evidence path is still bounded so
        // an unavailable database cannot indefinitely retain the request scope.
        using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _unitOfWork.RollbackAsync(auditTimeout.Token).ConfigureAwait(false);
            await _retryExecutor.ExecuteTransactionAsync(
                _unitOfWork,
                "auth.audit_failure",
                async token =>
                {
                    DateTime now = _clock.UtcNow;
                    var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["provider"] = provider,
                        ["reason"] = reason,
                    };
                    SecurityEvent securityEvent = IdentitySecurityEventFactory.Create(
                        "auth.login",
                        outcome,
                        SecurityEventSeverity.Warning,
                        context,
                        now,
                        _options.Retention.SecurityEventDays,
                        targetType: "identity_provider",
                        targetId: provider,
                        metadata: metadata);
                    await _securityEventRepository.AddWithPendingDeliveryAsync(
                        securityEvent,
                        token).ConfigureAwait(false);
                },
                auditTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Exception persistenceException = exception;
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await _unitOfWork.RollbackAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                persistenceException = new AggregateException(exception, cleanupException);
            }

            LogAuditPersistenceFailure(
                _logger,
                provider,
                outcome.ToString(),
                persistenceException);
        }
    }

    [LoggerMessage(
        EventId = 1220,
        Level = LogLevel.Error,
        Message = "Failed to persist auth.login security event for provider {Provider} with outcome {Outcome}")]
    private static partial void LogAuditPersistenceFailure(
        ILogger logger,
        string provider,
        string outcome,
        Exception exception);
}
