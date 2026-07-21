using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sharpbill.Application.Common;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public abstract partial class ProviderSigningKeyStore(
    string provider,
    ProviderDocumentClient documentClient,
    IClock clock,
    IOptions<SharpbillOptions> options,
    ILogger logger) : IDisposable
{
    private const int NegativeCacheCapacity = 512;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DateTime> _negativeKeys = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, SecurityKey>? _keys;
    private DateTime _fetchedAt;
    private int _failureCount;
    private DateTime _circuitUntil;
    private DateTime _unknownRefreshBlockedUntil;
    private readonly IdentityProviderOptions _options = options.Value.IdentityProviders;

    public async Task<SecurityKey> GetAsync(string keyId, CancellationToken cancellationToken)
    {
        ValidateKeyId(keyId);
        while (true)
        {
            CacheDecision decision = Decide(keyId);
            if (decision.Key is not null)
            {
                return decision.Key;
            }

            if (decision.Failure is not null)
            {
                throw decision.Failure;
            }

            bool entered = await _refreshGate.WaitAsync(
                TimeSpan.FromSeconds(_options.KeyRefreshWaitSeconds),
                cancellationToken).ConfigureAwait(false);
            if (!entered)
            {
                throw new IdentityProviderUnavailableException(
                    $"{provider} signing-key refresh timed out");
            }

            try
            {
                decision = Decide(keyId);
                if (decision.Key is not null)
                {
                    return decision.Key;
                }

                if (decision.Failure is not null)
                {
                    throw decision.Failure;
                }

                IReadOnlyDictionary<string, SecurityKey> refreshed;
                try
                {
                    using var document = await documentClient.FetchAsync(
                        Endpoint,
                        cancellationToken).ConfigureAwait(false);
                    refreshed = ParseKeys(document);
                    if (refreshed.Count == 0)
                    {
                        throw new IdentityProviderUnavailableException(
                            $"{provider} returned an empty signing-key document");
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not IdentityTokenException)
                {
                    SecurityKey? stale = RecordFailureAndGetFallback(keyId, exception);
                    if (stale is not null)
                    {
                        return stale;
                    }

                    throw new IdentityProviderUnavailableException(
                        $"{provider} signing keys are unavailable",
                        exception);
                }

                lock (_stateLock)
                {
                    DateTime now = clock.UtcNow;
                    _keys = refreshed;
                    _fetchedAt = now;
                    _failureCount = 0;
                    _circuitUntil = DateTime.MinValue;
                    if (refreshed.TryGetValue(keyId, out SecurityKey? requested))
                    {
                        return requested;
                    }

                    DateTime blockedUntil = now.AddSeconds(_options.UnknownKeyBackoffSeconds);
                    _unknownRefreshBlockedUntil = blockedUntil;
                    RememberNegativeKey(keyId, blockedUntil);
                    throw new IdentityTokenException("Unknown signing key id");
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }
    }

    protected abstract Uri Endpoint { get; }

    protected abstract IReadOnlyDictionary<string, SecurityKey> ParseKeys(System.Text.Json.JsonDocument document);

    private CacheDecision Decide(string keyId)
    {
        lock (_stateLock)
        {
            DateTime now = clock.UtcNow;
            PruneNegativeKeys(now);
            SecurityKey? key = null;
            bool known = _keys?.TryGetValue(keyId, out key) == true;
            TimeSpan age = _keys is null ? TimeSpan.MaxValue : now - _fetchedAt;
            if (known && age <= TimeSpan.FromSeconds(_options.KeyCacheTtlSeconds))
            {
                return new CacheDecision(key, null);
            }

            if (now < _circuitUntil)
            {
                if (known && age <= TimeSpan.FromSeconds(_options.KeyCacheStaleSeconds))
                {
                    return new CacheDecision(key, null);
                }

                return new CacheDecision(
                    null,
                    new IdentityProviderUnavailableException(
                        $"{provider} signing-key endpoint is unavailable"));
            }

            if (!known &&
                ((_negativeKeys.TryGetValue(keyId, out DateTime negativeUntil) && now < negativeUntil) ||
                 now < _unknownRefreshBlockedUntil))
            {
                return new CacheDecision(null, new IdentityTokenException("Unknown signing key id"));
            }

            return default;
        }
    }

    private SecurityKey? RecordFailureAndGetFallback(string keyId, Exception exception)
    {
        lock (_stateLock)
        {
            DateTime now = clock.UtcNow;
            _failureCount++;
            int exponent = Math.Min(_failureCount - 1, 16);
            double delaySeconds = Math.Min(
                _options.OutageBackoffInitialSeconds * Math.Pow(2, exponent),
                _options.OutageBackoffMaxSeconds);
            _circuitUntil = now.AddSeconds(delaySeconds);
            LogRefreshFailure(logger, provider, delaySeconds, exception);
            if (_keys?.TryGetValue(keyId, out SecurityKey? key) == true &&
                now - _fetchedAt <= TimeSpan.FromSeconds(_options.KeyCacheStaleSeconds))
            {
                return key;
            }

            return null;
        }
    }

    private void PruneNegativeKeys(DateTime now)
    {
        foreach (string key in _negativeKeys
            .Where(pair => pair.Value <= now)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            _negativeKeys.Remove(key);
        }
    }

    private void RememberNegativeKey(string keyId, DateTime expiresAt)
    {
        _negativeKeys.Remove(keyId);
        _negativeKeys.Add(keyId, expiresAt);
        if (_negativeKeys.Count <= NegativeCacheCapacity)
        {
            return;
        }

        string oldest = _negativeKeys.MinBy(static pair => pair.Value).Key;
        _negativeKeys.Remove(oldest);
    }

    private static void ValidateKeyId(string keyId)
    {
        if (string.IsNullOrEmpty(keyId) || keyId.Length > 256 || keyId.Any(static value => value < 0x20))
        {
            throw new IdentityTokenException("Missing or malformed signing key id");
        }
    }

    private readonly record struct CacheDecision(SecurityKey? Key, Exception? Failure);

    public void Dispose()
    {
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(
        EventId = 1210,
        Level = LogLevel.Warning,
        Message = "{Provider} signing-key refresh failed; circuit opened for {DelaySeconds} seconds")]
    private static partial void LogRefreshFailure(
        ILogger logger,
        string provider,
        double delaySeconds,
        Exception exception);
}
