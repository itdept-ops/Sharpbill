using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed partial class NonceService(
    INonceRepository nonceRepository,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<SharpbillOptions> options,
    ILogger<NonceService> logger,
    MySqlTransientRetryExecutor? retryExecutor = null) : INonceService
{
    private const int NonceByteLength = 32;
    private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(10);
    private readonly SharpbillOptions _options = options.Value;
    private readonly MySqlTransientRetryExecutor _retryExecutor =
        retryExecutor ?? MySqlTransientRetryExecutor.Default;

    public async Task<NonceResponse> IssueAsync(CancellationToken cancellationToken)
    {
        DateTime now = clock.UtcNow;
        int pruned = await nonceRepository.PruneExpiredAsync(
            now,
            _options.Retention.NonceBatchSize,
            cancellationToken).ConfigureAwait(false);
        int outstanding = await nonceRepository.CountActiveAsync(
            now,
            cancellationToken).ConfigureAwait(false);
        if (outstanding >= DomainLimits.MaxOutstandingLoginNonces)
        {
            throw CreateCapacityException(outstanding, pruned);
        }

        foreach (int shard in CreateRandomShardOrder())
        {
            string nonce = CreateNonceForShard(shard);
            var loginNonce = new LoginNonce
            {
                Nonce = nonce,
                CreatedAt = now,
                ExpiresAt = now.Add(NonceLifetime),
            };
            bool admitted = await _retryExecutor.ExecuteAsync(
                "nonce.admit",
                token => nonceRepository.TryAddWithinCapacityAsync(
                    loginNonce,
                    now,
                    DomainLimits.MaxOutstandingLoginNonces,
                    token),
                cancellationToken).ConfigureAwait(false);
            if (admitted)
            {
                LogIssueSucceeded(logger, outstanding + 1, pruned);
                return new NonceResponse { Nonce = nonce };
            }
        }

        outstanding = await nonceRepository.CountActiveAsync(now, cancellationToken).ConfigureAwait(false);
        throw CreateCapacityException(outstanding, pruned);
    }

    public async Task<bool> ConsumeAsync(string nonce, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(nonce))
        {
            LogConsumeMissing(logger);
            return false;
        }

        (bool Consumed, int Pruned) result = await _retryExecutor.ExecuteTransactionAsync(
            unitOfWork,
            "nonce.consume",
            async token =>
            {
                DateTime now = clock.UtcNow;
                int pruned = await nonceRepository.PruneExpiredAsync(
                    now,
                    _options.Retention.NonceBatchSize,
                    token).ConfigureAwait(false);
                bool consumed = await nonceRepository.ConsumeAsync(
                    nonce,
                    now,
                    token).ConfigureAwait(false);
                return (consumed, pruned);
            },
            cancellationToken).ConfigureAwait(false);
        LogConsume(
            logger,
            result.Consumed ? "succeeded" : "rejected_invalid_or_replayed",
            result.Pruned);
        return result.Consumed;
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string CreateNonceForShard(int shard)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(shard);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            shard,
            DomainLimits.LoginNonceAdmissionShards);
        byte[] value = RandomNumberGenerator.GetBytes(NonceByteLength);
        value[0] = (byte)((shard << 2) | (value[0] & 0b0000_0011));
        return Base64UrlEncode(value);
    }

    private static int[] CreateRandomShardOrder()
    {
        int[] shards = Enumerable.Range(0, DomainLimits.LoginNonceAdmissionShards).ToArray();
        for (int index = shards.Length - 1; index > 0; index--)
        {
            int swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (shards[index], shards[swapIndex]) = (shards[swapIndex], shards[index]);
        }

        return shards;
    }

    private ApiException CreateCapacityException(int outstanding, int pruned)
    {
        LogIssueRejectedCapacity(logger, outstanding, pruned);
        return new ApiException(
            503,
            "LOGIN_STATE_CAPACITY",
            "Sign-in is temporarily at capacity; retry shortly",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Retry-After"] = "30",
            });
    }

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
