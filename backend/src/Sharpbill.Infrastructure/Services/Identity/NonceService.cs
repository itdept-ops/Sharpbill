using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed partial class NonceService(
    INonceRepository nonceRepository,
    ISettingsRepository settingsRepository,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<SharpbillOptions> options,
    ILogger<NonceService> logger) : INonceService
{
    private const int MaximumOutstandingNonces = 5_000;
    private const int NonceByteLength = 32;
    private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim IssueGate = new(1, 1);
    private readonly SharpbillOptions _options = options.Value;

    public async Task<NonceResponse> IssueAsync(CancellationToken cancellationToken)
    {
        await IssueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTime now = clock.UtcNow;
                _ = await settingsRepository.GetAsync(
                    true,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new ApiException(500, "INTERNAL_ERROR", "Site settings row is missing");
                int pruned = await nonceRepository.PruneExpiredAsync(
                    now,
                    _options.Retention.NonceBatchSize,
                    cancellationToken).ConfigureAwait(false);
                int outstanding = await nonceRepository.CountActiveAsync(
                    now,
                    cancellationToken).ConfigureAwait(false);
                if (outstanding >= MaximumOutstandingNonces)
                {
                    await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                    LogIssueRejectedCapacity(logger, outstanding, pruned);
                    throw new ApiException(
                        503,
                        "LOGIN_STATE_CAPACITY",
                        "Sign-in is temporarily at capacity; retry shortly",
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["Retry-After"] = "30",
                        });
                }

                string nonce = Base64UrlEncode(RandomNumberGenerator.GetBytes(NonceByteLength));
                await nonceRepository.AddAsync(
                    new LoginNonce
                    {
                        Nonce = nonce,
                        CreatedAt = now,
                        ExpiresAt = now.Add(NonceLifetime),
                    },
                    cancellationToken).ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                LogIssueSucceeded(logger, outstanding + 1, pruned);
                return new NonceResponse { Nonce = nonce };
            }
            catch
            {
                await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            IssueGate.Release();
        }
    }

    public async Task<bool> ConsumeAsync(string nonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(nonce))
        {
            LogConsumeMissing(logger);
            return false;
        }

        await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTime now = clock.UtcNow;
            int pruned = await nonceRepository.PruneExpiredAsync(
                now,
                _options.Retention.NonceBatchSize,
                cancellationToken).ConfigureAwait(false);
            bool consumed = await nonceRepository.ConsumeAsync(
                nonce,
                now,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            LogConsume(logger, consumed ? "succeeded" : "rejected_invalid_or_replayed", pruned);
            return consumed;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Information,
        Message = "OIDC nonce issue succeeded; outstanding={Outstanding}, pruned={Pruned}")]
    private static partial void LogIssueSucceeded(ILogger logger, int outstanding, int pruned);

    [LoggerMessage(
        EventId = 1201,
        Level = LogLevel.Warning,
        Message = "OIDC nonce issue rejected at capacity; outstanding={Outstanding}, pruned={Pruned}")]
    private static partial void LogIssueRejectedCapacity(ILogger logger, int outstanding, int pruned);

    [LoggerMessage(
        EventId = 1202,
        Level = LogLevel.Information,
        Message = "OIDC nonce consume {Outcome}; pruned={Pruned}")]
    private static partial void LogConsume(ILogger logger, string outcome, int pruned);

    [LoggerMessage(
        EventId = 1203,
        Level = LogLevel.Information,
        Message = "OIDC nonce consume rejected_missing")]
    private static partial void LogConsumeMissing(ILogger logger);
}
