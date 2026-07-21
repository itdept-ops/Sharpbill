using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Health;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Operations;

public sealed class HealthService(
    IHealthRepository health,
    ISettingsRepository settings,
    IOptions<SharpbillOptions> options,
    TimeProvider timeProvider) : IHealthService
{
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);
    private static readonly object CacheGate = new();
    private static (DateTimeOffset Expires, ReadinessResponse Response, bool Ready)? _cached;

    public LivenessResponse GetLiveness() => new();

    public async Task<(ReadinessResponse Response, bool IsReady)> GetReadinessAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        lock (CacheGate)
        {
            if (_cached is { } cached && cached.Expires > now)
            {
                return (cached.Response, cached.Ready);
            }
        }

        if (!await ProbeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return (NotReady("probe_in_progress", "unknown", "unknown", "unknown", "unknown"), false);
        }

        try
        {
            lock (CacheGate)
            {
                if (_cached is { } cached && cached.Expires > now)
                {
                    return (cached.Response, cached.Ready);
                }
            }

            (ReadinessResponse Response, bool Ready) result = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            lock (CacheGate)
            {
                _cached = (timeProvider.GetUtcNow().AddSeconds(2), result.Response, result.Ready);
            }

            return result;
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    private async Task<(ReadinessResponse Response, bool Ready)> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await health.CanConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                return (NotReady("error", "unknown", "unknown", "unknown", "unknown"), false);
            }

            IReadOnlySet<string> heads = await health.GetSchemaHeadsAsync(cancellationToken).ConfigureAwait(false);
            if (!heads.SetEquals(["0021"]))
            {
                return (NotReady("ok", "mismatch", "unknown", "unknown", "unknown"), false);
            }

            SiteSettings? site = await settings.GetAsync(false, cancellationToken).ConfigureAwait(false);
            SharpbillOptions configuration = options.Value;
            bool google = site?.AllowGoogle == true &&
                !string.IsNullOrWhiteSpace(configuration.IdentityProviders.GoogleClientId);
            bool microsoft = site?.AllowMicrosoft == true &&
                !string.IsNullOrWhiteSpace(configuration.IdentityProviders.MicrosoftClientId);
            bool development = configuration.IsLocal && configuration.DevelopmentAuthentication.Enabled &&
                !string.IsNullOrWhiteSpace(configuration.DevelopmentAuthentication.Secret);
            bool unsafeDefault = await health.HasUnsafeAdministratorDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (!(google || microsoft || development))
            {
                return (NotReady("ok", "ok", "unavailable", "unknown", unsafeDefault ? "unsafe" : "ok"), false);
            }

            if (unsafeDefault)
            {
                return (NotReady("ok", "ok", "ok", "unknown", "unsafe"), false);
            }

            if (!await health.HasReachableAdministratorAsync(cancellationToken).ConfigureAwait(false))
            {
                return (NotReady("ok", "ok", "ok", "unavailable", "ok"), false);
            }

            return (new ReadinessResponse
            {
                Status = "ready",
                Database = "ok",
                Schema = "ok",
                IdentityProvider = "ok",
                Administration = "ok",
                AdmissionPolicy = "ok",
            }, true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (NotReady("error", "unknown", "unknown", "unknown", "unknown"), false);
        }
    }

    private static ReadinessResponse NotReady(
        string database,
        string schema,
        string identityProvider,
        string administration,
        string admissionPolicy) => new()
        {
            Status = "not_ready",
            Database = database,
            Schema = schema,
            IdentityProvider = identityProvider,
            Administration = administration,
            AdmissionPolicy = admissionPolicy,
        };
}
