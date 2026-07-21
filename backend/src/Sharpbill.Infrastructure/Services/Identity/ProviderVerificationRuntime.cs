using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Common;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class ProviderVerificationRuntime(
    IOptions<SharpbillOptions> options,
    IClock clock) : IDisposable
{
    private const int MaximumReplayEntries = 10_000;
    private readonly SemaphoreSlim _verificationGate = new(
        options.Value.IdentityProviders.VerificationMaxConcurrency,
        options.Value.IdentityProviders.VerificationMaxConcurrency);
    private readonly SemaphoreSlim _networkGate = new(
        options.Value.IdentityProviders.NetworkMaxConcurrency,
        options.Value.IdentityProviders.NetworkMaxConcurrency);
    private readonly object _replayLock = new();
    private readonly Dictionary<string, DateTime> _seenTokens = new(StringComparer.Ordinal);
    private readonly PriorityQueue<(string Hash, DateTime ExpiresAt), DateTime> _replayExpirations = new();

    public DateTime UtcNow => clock.UtcNow;

    public async ValueTask<IDisposable> AcquireVerificationAsync(CancellationToken cancellationToken)
    {
        if (!await _verificationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new IdentityProviderUnavailableException(
                "Identity-provider verification capacity is exhausted");
        }

        return new GateLease(_verificationGate);
    }

    public async ValueTask<IDisposable> AcquireNetworkAsync(CancellationToken cancellationToken)
    {
        if (!await _networkGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new IdentityProviderUnavailableException(
                "Identity-provider key retrieval capacity is exhausted");
        }

        return new GateLease(_networkGate);
    }

    public bool IsReplay(string rawToken, DateTime replayUntil)
    {
        DateTime now = clock.UtcNow;
        if (replayUntil <= now)
        {
            return false;
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
        lock (_replayLock)
        {
            while (_replayExpirations.TryPeek(out (string Hash, DateTime ExpiresAt) entry, out DateTime expiry) &&
                expiry <= now)
            {
                _ = _replayExpirations.Dequeue();
                if (_seenTokens.TryGetValue(entry.Hash, out DateTime currentExpiry) &&
                    currentExpiry == entry.ExpiresAt)
                {
                    _seenTokens.Remove(entry.Hash);
                }
            }

            if (_seenTokens.ContainsKey(hash))
            {
                return true;
            }

            if (_seenTokens.Count >= MaximumReplayEntries)
            {
                // The database-backed nonce is the authoritative cross-instance replay guard.
                // Keep this process-local cache bounded as defense in depth, but degrade by
                // evicting an old entry instead of turning capacity into a global login lockout.
                EvictEarliestReplayEntry();
            }

            _seenTokens.Add(hash, replayUntil);
            _replayExpirations.Enqueue((hash, replayUntil), replayUntil);
            return false;
        }
    }

    private void EvictEarliestReplayEntry()
    {
        while (_replayExpirations.TryDequeue(
            out (string Hash, DateTime ExpiresAt) entry,
            out _))
        {
            if (_seenTokens.TryGetValue(entry.Hash, out DateTime currentExpiry) &&
                currentExpiry == entry.ExpiresAt)
            {
                _seenTokens.Remove(entry.Hash);
                return;
            }
        }

        // Defensive recovery if the index and queue ever diverge.
        if (_seenTokens.Count > 0)
        {
            _seenTokens.Remove(_seenTokens.Keys.First());
        }
    }

    public void Dispose()
    {
        _verificationGate.Dispose();
        _networkGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class GateLease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
