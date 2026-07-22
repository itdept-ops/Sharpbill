using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Sharpbill.Infrastructure.Configuration;

public sealed class SharpbillOptionsSetup(IConfiguration configuration) : IConfigureOptions<SharpbillOptions>
{
    public void Configure(SharpbillOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AppEnvironment = Get("APP_ENV", "production");
        options.PublicOrigin = NullIfWhiteSpace(configuration["PUBLIC_ORIGIN"]);
        (options.TrustedProxies, options.TrustedProxyNetworks) = ParseTrustedProxies();

        options.Database = ConfigureDatabase();

        options.Session = new SessionOptions
        {
            ActiveSecret = Get("SESSION_JWT_SECRET", string.Empty),
            PreviousSecrets = Split("SESSION_JWT_PREVIOUS_SECRETS"),
            Issuer = Get("SESSION_JWT_ISSUER", "sharpbill"),
            Audience = Get("SESSION_JWT_AUDIENCE", "sharpbill-web"),
            LifetimeHours = GetInt("SESSION_TTL_HOURS", 8),
            SecureCookie = GetBool("COOKIE_SECURE", !options.IsLocal),
            MaxActiveSessionsPerUser = GetInt("MAX_ACTIVE_SESSIONS_PER_USER", 20),
        };

        options.IdentityProviders = new IdentityProviderOptions
        {
            GoogleClientId = NullIfWhiteSpace(configuration["GOOGLE_CLIENT_ID"]),
            MicrosoftClientId = NullIfWhiteSpace(configuration["AZURE_CLIENT_ID"]),
            MicrosoftAdminTenantId = NullIfWhiteSpace(configuration["AZURE_ADMIN_TENANT_ID"]),
            GoogleAdminSubjects = ToSet("GOOGLE_ADMIN_SUBJECTS", StringComparer.Ordinal),
            MicrosoftAdminObjectIds = ToSet("AZURE_ADMIN_OBJECT_IDS", StringComparer.OrdinalIgnoreCase),
            DevelopmentAdminEmails = ToSet("ADMIN_EMAILS", StringComparer.OrdinalIgnoreCase),
            VerificationMaxConcurrency = GetInt("IDP_VERIFICATION_MAX_CONCURRENCY", 8),
            NetworkMaxConcurrency = GetInt("IDP_NETWORK_MAX_CONCURRENCY", 2),
            ConnectTimeoutSeconds = GetInt("IDP_HTTP_CONNECT_TIMEOUT_SECONDS", 2),
            ReadTimeoutSeconds = GetInt("IDP_HTTP_READ_TIMEOUT_SECONDS", 3),
            KeyCacheTtlSeconds = GetInt("IDP_KEY_CACHE_TTL_SECONDS", 3600),
            KeyCacheStaleSeconds = GetInt("IDP_KEY_CACHE_STALE_SECONDS", 86400),
            KeyRefreshWaitSeconds = GetInt("IDP_KEY_REFRESH_WAIT_SECONDS", 6),
            UnknownKeyBackoffSeconds = GetInt("IDP_UNKNOWN_KID_BACKOFF_SECONDS", 10),
            OutageBackoffInitialSeconds = GetInt("IDP_OUTAGE_BACKOFF_INITIAL_SECONDS", 2),
            OutageBackoffMaxSeconds = GetInt("IDP_OUTAGE_BACKOFF_MAX_SECONDS", 60),
            KeyDocumentMaxBytes = GetInt("IDP_KEY_DOCUMENT_MAX_BYTES", 1_048_576),
        };

        options.Retention = new RetentionOptions
        {
            SessionDays = GetInt("SESSION_RETENTION_DAYS", 30),
            RequestLogDays = GetInt("REQUEST_LOG_RETENTION_DAYS", 90),
            SecurityEventDays = GetInt("SECURITY_EVENT_RETENTION_DAYS", 400),
            LegalAcceptanceDays = GetInt("LEGAL_ACCEPTANCE_RETENTION_DAYS", 2555),
            PreciseLocationHours = GetInt("PRECISE_LOCATION_RETENTION_HOURS", 24),
            PendingAccountDays = GetInt("PENDING_ACCOUNT_RETENTION_DAYS", 30),
            DisabledAccountDays = GetInt("DISABLED_ACCOUNT_RETENTION_DAYS", 365),
            AccountErasureGraceDays = GetInt("ACCOUNT_ERASURE_GRACE_DAYS", 30),
            WorkerIntervalSeconds = GetInt("RETENTION_WORKER_INTERVAL_SECONDS", 3600),
            WorkerMaxBatchesPerCycle = GetInt("RETENTION_WORKER_MAX_BATCHES_PER_CYCLE", 10),
            SessionBatchSize = GetInt("SESSION_PRUNE_BATCH_SIZE", 500),
            RequestLogBatchSize = GetInt("REQUEST_LOG_PRUNE_BATCH_SIZE", 2000),
            NonceBatchSize = GetInt("NONCE_PRUNE_BATCH_SIZE", 500),
            PreciseLocationBatchSize = GetInt("PRECISE_LOCATION_PRUNE_BATCH_SIZE", 500),
            AccountBatchSize = GetInt("ACCOUNT_RETENTION_PRUNE_BATCH_SIZE", 100),
            SecurityEventBatchSize = GetInt("SECURITY_EVENT_PRUNE_BATCH_SIZE", 500),
            LegalAcceptanceBatchSize = GetInt("LEGAL_ACCEPTANCE_PRUNE_BATCH_SIZE", 500),
        };

        options.RequestPipeline = new RequestPipelineOptions
        {
            BodyLimitBytes = GetLong("REQUEST_BODY_MAX_BYTES", 1_048_576),
            ExportMaxBytes = GetInt("EXPORT_MAX_BYTES", 25 * 1024 * 1024),
            ExportMaxConcurrency = GetInt("EXPORT_MAX_CONCURRENCY", 2),
            RequestLogQueueCapacity = GetInt("REQUEST_LOG_QUEUE_CAPACITY", 2048),
            RequestLogShutdownTimeoutSeconds = GetInt("REQUEST_LOG_SHUTDOWN_TIMEOUT_SECONDS", 5),
            RetentionShutdownTimeoutSeconds = GetInt("RETENTION_WORKER_SHUTDOWN_TIMEOUT_SECONDS", 10),
        };

        options.DevelopmentAuthentication = new DevelopmentAuthenticationOptions
        {
            Enabled = GetBool("DEV_AUTH_ENABLED", false),
            Secret = NullIfWhiteSpace(configuration["DEV_AUTH_SECRET"]),
        };
    }

    private string Get(string key, string fallback) => NullIfWhiteSpace(configuration[key]) ?? fallback;

    private DatabaseOptions ConfigureDatabase()
    {
        string? completeConnectionString = NullIfWhiteSpace(
            configuration.GetConnectionString("Sharpbill"));
        string[] discreteConnectionKeys =
        [
            "DB_HOST",
            "DB_PORT",
            "DB_NAME",
            "DB_USER",
            "DB_PASSWORD",
            "DB_REQUIRE_TLS",
            "DB_TLS_CA_PATH",
        ];
        if (completeConnectionString is not null &&
            discreteConnectionKeys.Any(key => configuration[key] is not null))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Sharpbill and discrete DB_* connection settings are mutually exclusive.");
        }

        DatabaseOptions database;
        if (completeConnectionString is null)
        {
            database = new DatabaseOptions
            {
                Host = Get("DB_HOST", string.Empty),
                Port = GetUInt("DB_PORT", 3306),
                Name = Get("DB_NAME", string.Empty),
                User = Get("DB_USER", string.Empty),
                Password = Get("DB_PASSWORD", string.Empty),
                RequireTls = GetBool("DB_REQUIRE_TLS", false),
                TlsCaPath = NullIfWhiteSpace(configuration["DB_TLS_CA_PATH"]),
            };
        }
        else
        {
            MySqlConnectionStringBuilder parsed;
            try
            {
                parsed = new MySqlConnectionStringBuilder(completeConnectionString);
            }
            catch (ArgumentException exception)
            {
                throw new FormatException(
                    $"ConnectionStrings:Sharpbill is invalid: {exception.Message}",
                    exception);
            }

            if (parsed.SslMode is not (MySqlSslMode.Disabled or MySqlSslMode.VerifyFull))
            {
                throw new FormatException(
                    "ConnectionStrings:Sharpbill must use SslMode=Disabled locally or " +
                    "SslMode=VerifyFull with SslCa for verified TLS.");
            }

            database = new DatabaseOptions
            {
                Host = parsed.Server,
                Port = parsed.Port,
                Name = parsed.Database,
                User = parsed.UserID,
                Password = parsed.Password,
                RequireTls = parsed.SslMode == MySqlSslMode.VerifyFull,
                TlsCaPath = NullIfWhiteSpace(parsed.SslCa),
            };
        }

        database.PoolSize = GetUInt("DB_POOL_SIZE", 5);
        database.MaxOverflow = GetUInt("DB_MAX_OVERFLOW", 5);
        database.PoolTimeoutSeconds = GetUInt("DB_POOL_TIMEOUT_SECONDS", 10);
        database.PoolRecycleSeconds = GetUInt("DB_POOL_RECYCLE_SECONDS", 280);
        database.ConnectTimeoutSeconds = GetUInt("DB_CONNECT_TIMEOUT_SECONDS", 5);
        database.ReadTimeoutSeconds = GetUInt("DB_READ_TIMEOUT_SECONDS", 30);
        database.WriteTimeoutSeconds = GetUInt("DB_WRITE_TIMEOUT_SECONDS", 30);
        return database;
    }

    private int GetInt(string key, int fallback) => Parse(key, fallback, int.Parse);

    private long GetLong(string key, long fallback) => Parse(key, fallback, long.Parse);

    private uint GetUInt(string key, uint fallback) => Parse(key, fallback, uint.Parse);

    private bool GetBool(string key, bool fallback)
    {
        string? raw = NullIfWhiteSpace(configuration[key]);
        return raw is null ? fallback : bool.Parse(raw);
    }

    private T Parse<T>(string key, T fallback, Func<string, NumberStyles, IFormatProvider, T> parser)
        where T : struct
    {
        string? raw = NullIfWhiteSpace(configuration[key]);
        return raw is null ? fallback : parser(raw, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private string[] Split(string key) => (configuration[key] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private HashSet<string> ToSet(string key, StringComparer comparer) =>
        new HashSet<string>(Split(key), comparer);

    private (IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks) ParseTrustedProxies()
    {
        var proxies = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        foreach (string value in Split("TRUSTED_PROXY_IPS"))
        {
            if (value.Contains('/', StringComparison.Ordinal))
            {
                if (!IPNetwork.TryParse(value, out IPNetwork network))
                {
                    throw new FormatException($"TRUSTED_PROXY_IPS contains an invalid CIDR network: {value}");
                }

                networks.Add(network);
            }
            else if (IPAddress.TryParse(value, out IPAddress? address))
            {
                proxies.Add(address);
            }
            else
            {
                throw new FormatException($"TRUSTED_PROXY_IPS contains an invalid IP address: {value}");
            }
        }

        return (proxies, networks);
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
