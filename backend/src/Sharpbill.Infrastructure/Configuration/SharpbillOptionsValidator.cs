using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Sharpbill.Infrastructure.Configuration;

public sealed class SharpbillOptionsValidator : IValidateOptions<SharpbillOptions>
{
    private static readonly Regex GoogleClientIdPattern = new(
        "^[0-9]{6,32}-[A-Za-z0-9_-]{8,128}\\.apps\\.googleusercontent\\.com$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public ValidateOptionsResult Validate(string? name, SharpbillOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        if (options.AppEnvironment is not ("local" or "production"))
        {
            failures.Add("APP_ENV must be either local or production.");
        }

        RequireText(options.Database.Host, nameof(options.Database.Host), failures);
        RequireIdentifier(options.Database.Name, nameof(options.Database.Name), failures);
        RequireIdentifier(options.Database.User, nameof(options.Database.User), failures);
        if (string.IsNullOrEmpty(options.Database.Password))
        {
            failures.Add("DB_PASSWORD is required.");
        }

        InRange(options.Database.Port, 1, 65_535, "DB_PORT", failures);
        InRange(options.Database.PoolSize, 1, 50, "DB_POOL_SIZE", failures);
        InRange(options.Database.MaxOverflow, 0, 50, "DB_MAX_OVERFLOW", failures);
        InRange(options.Database.PoolTimeoutSeconds, 1, 120, "DB_POOL_TIMEOUT_SECONDS", failures);
        InRange(options.Database.PoolRecycleSeconds, 30, 3_600, "DB_POOL_RECYCLE_SECONDS", failures);
        InRange(options.Database.ConnectTimeoutSeconds, 1, 60, "DB_CONNECT_TIMEOUT_SECONDS", failures);
        InRange(options.Database.ReadTimeoutSeconds, 1, 300, "DB_READ_TIMEOUT_SECONDS", failures);
        InRange(options.Database.WriteTimeoutSeconds, 1, 300, "DB_WRITE_TIMEOUT_SECONDS", failures);
        InRange(options.Session.LifetimeHours, 1, 168, "SESSION_TTL_HOURS", failures);
        InRange(options.Session.MaxActiveSessionsPerUser, 1, 100, "MAX_ACTIVE_SESSIONS_PER_USER", failures);
        InRange(options.RequestPipeline.BodyLimitBytes, 16_384, 10_485_760, "REQUEST_BODY_MAX_BYTES", failures);
        InRange(options.RequestPipeline.ExportMaxBytes, 1_048_576, 104_857_600,
            "EXPORT_MAX_BYTES", failures);
        InRange(options.RequestPipeline.ExportMaxConcurrency, 1, 32,
            "EXPORT_MAX_CONCURRENCY", failures);
        InRange(options.RequestPipeline.RequestLogQueueCapacity, 100, 100_000, "REQUEST_LOG_QUEUE_CAPACITY", failures);
        InRange(options.RequestPipeline.RequestLogShutdownTimeoutSeconds, 1, 30,
            "REQUEST_LOG_SHUTDOWN_TIMEOUT_SECONDS", failures);
        InRange(options.RequestPipeline.RetentionShutdownTimeoutSeconds, 1, 60,
            "RETENTION_WORKER_SHUTDOWN_TIMEOUT_SECONDS", failures);
        InRange(options.Retention.PreciseLocationHours, 1, 720, "PRECISE_LOCATION_RETENTION_HOURS", failures);
        InRange(options.Retention.PendingAccountDays, 1, 365, "PENDING_ACCOUNT_RETENTION_DAYS", failures);
        InRange(options.Retention.DisabledAccountDays, 30, 2_555, "DISABLED_ACCOUNT_RETENTION_DAYS", failures);
        InRange(options.Retention.AccountErasureGraceDays, 1, 90, "ACCOUNT_ERASURE_GRACE_DAYS", failures);
        InRange(options.Retention.SessionDays, 1, 365, "SESSION_RETENTION_DAYS", failures);
        InRange(options.Retention.RequestLogDays, 1, 365, "REQUEST_LOG_RETENTION_DAYS", failures);
        InRange(options.Retention.SecurityEventDays, 30, 2_555, "SECURITY_EVENT_RETENTION_DAYS", failures);
        InRange(options.Retention.LegalAcceptanceDays, 1, 3650, "LEGAL_ACCEPTANCE_RETENTION_DAYS", failures);
        InRange(options.Retention.WorkerIntervalSeconds, 60, 86_400, "RETENTION_WORKER_INTERVAL_SECONDS", failures);
        InRange(options.Retention.WorkerMaxBatchesPerCycle, 1, 100, "RETENTION_WORKER_MAX_BATCHES_PER_CYCLE", failures);
        InRange(options.Retention.SessionBatchSize, 100, 10_000, "SESSION_PRUNE_BATCH_SIZE", failures);
        InRange(options.Retention.RequestLogBatchSize, 100, 10_000, "REQUEST_LOG_PRUNE_BATCH_SIZE", failures);
        InRange(options.Retention.NonceBatchSize, 100, 10_000, "NONCE_PRUNE_BATCH_SIZE", failures);
        InRange(options.Retention.PreciseLocationBatchSize, 10, 10_000,
            "PRECISE_LOCATION_PRUNE_BATCH_SIZE", failures);
        InRange(options.Retention.AccountBatchSize, 10, 1_000,
            "ACCOUNT_RETENTION_PRUNE_BATCH_SIZE", failures);
        InRange(options.Retention.SecurityEventBatchSize, 10, 10_000,
            "SECURITY_EVENT_PRUNE_BATCH_SIZE", failures);
        InRange(options.Retention.LegalAcceptanceBatchSize, 10, 10_000,
            "LEGAL_ACCEPTANCE_PRUNE_BATCH_SIZE", failures);

        ValidateSessionSecrets(options, failures);
        ValidateIdentityProviders(options, failures);
        ValidatePublicOrigin(options, failures);
        ValidateDevelopmentAuthentication(options, failures);

        if (!options.IsLocal && !options.Database.RequireTls)
        {
            failures.Add("DB_REQUIRE_TLS must be true outside local development.");
        }

        if (options.Database.RequireTls && string.IsNullOrWhiteSpace(options.Database.TlsCaPath))
        {
            failures.Add("DB_TLS_CA_PATH is required when DB_REQUIRE_TLS is true.");
        }

        if (!options.IsLocal && !options.Session.SecureCookie)
        {
            failures.Add("COOKIE_SECURE must be true outside local development.");
        }

        if (!options.IsLocal && options.TrustedProxyNetworks.Any(static network => network.PrefixLength == 0))
        {
            failures.Add("TRUSTED_PROXY_IPS cannot trust a world-wide CIDR outside local development.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSessionSecrets(SharpbillOptions options, List<string> failures)
    {
        ValidateSecret(options.Session.ActiveSecret, "SESSION_JWT_SECRET", failures);
        if (options.Session.Issuer.Length is < 3 or > 255)
        {
            failures.Add("SESSION_JWT_ISSUER must contain 3-255 characters.");
        }

        if (options.Session.Audience.Length is < 3 or > 255)
        {
            failures.Add("SESSION_JWT_AUDIENCE must contain 3-255 characters.");
        }

        if (options.Session.PreviousSecrets.Count > 5)
        {
            failures.Add("SESSION_JWT_PREVIOUS_SECRETS supports at most five rotation keys.");
        }

        HashSet<string> unique = new(StringComparer.Ordinal) { options.Session.ActiveSecret };
        foreach (string secret in options.Session.PreviousSecrets)
        {
            ValidateSecret(secret, "SESSION_JWT_PREVIOUS_SECRETS", failures);

            if (!unique.Add(secret))
            {
                failures.Add("JWT rotation secrets must be unique.");
            }
        }

        int keyIds = unique.Select(static secret => Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..16])
            .Distinct(StringComparer.Ordinal).Count();
        if (keyIds != unique.Count)
        {
            failures.Add("JWT signing-key IDs collide; replace one configured secret.");
        }
    }

    private static void ValidateSecret(string value, string label, List<string> failures)
    {
        if (value.Length < 32 || value.Contains("replace-me", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{label} must contain at least 32 characters and not be a placeholder.");
        }

        if (value.Distinct().Take(8).Count() < 8)
        {
            failures.Add($"{label} has too little entropy.");
        }
    }

    private static void ValidateIdentityProviders(SharpbillOptions options, List<string> failures)
    {
        IdentityProviderOptions identity = options.IdentityProviders;
        InRange(identity.VerificationMaxConcurrency, 1, 64, "IDP_VERIFICATION_MAX_CONCURRENCY", failures);
        InRange(identity.NetworkMaxConcurrency, 1, 8, "IDP_NETWORK_MAX_CONCURRENCY", failures);
        InRange(identity.ConnectTimeoutSeconds, 1, 10, "IDP_HTTP_CONNECT_TIMEOUT_SECONDS", failures);
        InRange(identity.ReadTimeoutSeconds, 1, 15, "IDP_HTTP_READ_TIMEOUT_SECONDS", failures);
        InRange(identity.KeyCacheTtlSeconds, 60, 86_400, "IDP_KEY_CACHE_TTL_SECONDS", failures);
        InRange(identity.KeyCacheStaleSeconds, 300, 604_800, "IDP_KEY_CACHE_STALE_SECONDS", failures);
        InRange(identity.KeyRefreshWaitSeconds, 1, 30, "IDP_KEY_REFRESH_WAIT_SECONDS", failures);
        InRange(identity.UnknownKeyBackoffSeconds, 1, 300, "IDP_UNKNOWN_KID_BACKOFF_SECONDS", failures);
        InRange(identity.OutageBackoffInitialSeconds, 1, 30, "IDP_OUTAGE_BACKOFF_INITIAL_SECONDS", failures);
        InRange(identity.OutageBackoffMaxSeconds, 1, 600, "IDP_OUTAGE_BACKOFF_MAX_SECONDS", failures);
        InRange(identity.KeyDocumentMaxBytes, 16_384, 4_194_304, "IDP_KEY_DOCUMENT_MAX_BYTES", failures);
        if (identity.KeyCacheStaleSeconds < identity.KeyCacheTtlSeconds)
        {
            failures.Add("IDP_KEY_CACHE_STALE_SECONDS must be greater than or equal to IDP_KEY_CACHE_TTL_SECONDS.");
        }

        if (identity.OutageBackoffMaxSeconds < identity.OutageBackoffInitialSeconds)
        {
            failures.Add("IDP_OUTAGE_BACKOFF_MAX_SECONDS must be greater than or equal to IDP_OUTAGE_BACKOFF_INITIAL_SECONDS.");
        }

        bool google = !string.IsNullOrWhiteSpace(identity.GoogleClientId);
        bool microsoft = !string.IsNullOrWhiteSpace(identity.MicrosoftClientId);
        if (google && !GoogleClientIdPattern.IsMatch(identity.GoogleClientId!))
        {
            failures.Add("GOOGLE_CLIENT_ID must be a valid Google OAuth web client identifier.");
        }

        if (microsoft && !Guid.TryParseExact(identity.MicrosoftClientId, "D", out _))
        {
            failures.Add("AZURE_CLIENT_ID must be a canonical UUID.");
        }

        if (identity.MicrosoftAdminTenantId is not null &&
            !Guid.TryParseExact(identity.MicrosoftAdminTenantId, "D", out _))
        {
            failures.Add("AZURE_ADMIN_TENANT_ID must be a canonical UUID.");
        }

        if (identity.MicrosoftAdminObjectIds.Any(static value => !Guid.TryParseExact(value, "D", out _)))
        {
            failures.Add("AZURE_ADMIN_OBJECT_IDS must contain canonical UUIDs.");
        }

        if (!options.IsLocal)
        {
            if (!(google || microsoft))
            {
                failures.Add("At least one identity provider must be configured in production.");
            }

            if (identity.DevelopmentAdminEmails.Count > 0)
            {
                failures.Add("ADMIN_EMAILS is local-only; use immutable provider subjects in production.");
            }

            if (identity.MicrosoftAdminObjectIds.Count > 0 && identity.MicrosoftAdminTenantId is null)
            {
                failures.Add("AZURE_ADMIN_TENANT_ID is required with AZURE_ADMIN_OBJECT_IDS in production.");
            }
        }
    }

    private static void ValidatePublicOrigin(SharpbillOptions options, List<string> failures)
    {
        if (options.PublicOrigin is null)
        {
            if (!options.IsLocal)
            {
                failures.Add("PUBLIC_ORIGIN is required outside local development.");
            }

            return;
        }

        if (!Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out Uri? origin)
            || (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps)
            || origin.PathAndQuery != "/"
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Fragment))
        {
            failures.Add("PUBLIC_ORIGIN must be an absolute HTTP(S) origin without path, query, credentials, or fragment.");
            return;
        }

        if (!options.IsLocal && origin.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add("PUBLIC_ORIGIN must use HTTPS outside local development.");
        }
    }

    private static void ValidateDevelopmentAuthentication(SharpbillOptions options, List<string> failures)
    {
        if (!options.DevelopmentAuthentication.Enabled)
        {
            return;
        }

        if (!options.IsLocal)
        {
            failures.Add("DEV_AUTH_ENABLED is only permitted when APP_ENV=local.");
        }

        if (!DevelopmentAuthenticationGuard.IsStrongIndependentSecret(
            options.DevelopmentAuthentication.Secret,
            options.Session.ActiveSecret))
        {
            failures.Add(
                "DEV_AUTH_SECRET must be an independent, non-placeholder secret with at least " +
                "32 characters and eight distinct characters when development authentication is enabled.");
        }
    }

    private static void RequireText(string value, string name, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{name} is required.");
        }
    }

    private static void RequireIdentifier(string value, string name, List<string> failures)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value.Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
        {
            failures.Add($"{name} must be a 1-64 character ASCII identifier.");
        }
    }

    private static void InRange(long value, long minimum, long maximum, string name, List<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{name} must be between {minimum} and {maximum}.");
        }
    }
}
