using System.Net;

namespace Sharpbill.Infrastructure.Configuration;

public sealed class SharpbillOptions
{
    public string AppEnvironment { get; set; } = "production";

    public string? PublicOrigin { get; set; }

    public IReadOnlyList<IPAddress> TrustedProxies { get; set; } = [];

    public IReadOnlyList<IPNetwork> TrustedProxyNetworks { get; set; } = [];

    public DatabaseOptions Database { get; set; } = new();

    public SessionOptions Session { get; set; } = new();

    public IdentityProviderOptions IdentityProviders { get; set; } = new();

    public RetentionOptions Retention { get; set; } = new();

    public RequestPipelineOptions RequestPipeline { get; set; } = new();

    public DevelopmentAuthenticationOptions DevelopmentAuthentication { get; set; } = new();

    public bool IsLocal => string.Equals(AppEnvironment, "local", StringComparison.OrdinalIgnoreCase);
}

public sealed class DatabaseOptions
{
    public string Host { get; set; } = "localhost";

    public uint Port { get; set; } = 3306;

    public string Name { get; set; } = "sharpbill";

    public string User { get; set; } = "sharpbill";

    public string Password { get; set; } = string.Empty;

    public bool RequireTls { get; set; }

    public string? TlsCaPath { get; set; }

    public uint PoolSize { get; set; } = 5;

    public uint MaxOverflow { get; set; } = 5;

    public uint PoolTimeoutSeconds { get; set; } = 10;

    public uint PoolRecycleSeconds { get; set; } = 280;

    public uint ConnectTimeoutSeconds { get; set; } = 5;

    public uint ReadTimeoutSeconds { get; set; } = 30;

    public uint WriteTimeoutSeconds { get; set; } = 30;
}

public sealed class SessionOptions
{
    public string ActiveSecret { get; set; } = string.Empty;

    public IReadOnlyList<string> PreviousSecrets { get; set; } = [];

    public string Issuer { get; set; } = "sharpbill";

    public string Audience { get; set; } = "sharpbill-web";

    public int LifetimeHours { get; set; } = 8;

    public bool SecureCookie { get; set; } = true;

    public int MaxActiveSessionsPerUser { get; set; } = 20;

    public string LocalCookieName { get; set; } = "session";

    public string ProductionCookieName { get; set; } = "__Host-session";
}

public sealed class IdentityProviderOptions
{
    public string? GoogleClientId { get; set; }

    public string? MicrosoftClientId { get; set; }

    public string? MicrosoftAdminTenantId { get; set; }

    public IReadOnlySet<string> GoogleAdminSubjects { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlySet<string> MicrosoftAdminObjectIds { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> DevelopmentAdminEmails { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public int VerificationMaxConcurrency { get; set; } = 8;

    public int NetworkMaxConcurrency { get; set; } = 2;

    public int ConnectTimeoutSeconds { get; set; } = 2;

    public int ReadTimeoutSeconds { get; set; } = 3;

    public int KeyCacheTtlSeconds { get; set; } = 3600;

    public int KeyCacheStaleSeconds { get; set; } = 86400;

    public int KeyRefreshWaitSeconds { get; set; } = 6;

    public int UnknownKeyBackoffSeconds { get; set; } = 10;

    public int OutageBackoffInitialSeconds { get; set; } = 2;

    public int OutageBackoffMaxSeconds { get; set; } = 60;

    public int KeyDocumentMaxBytes { get; set; } = 1_048_576;
}

public sealed class RetentionOptions
{
    public int SessionDays { get; set; } = 30;

    public int RequestLogDays { get; set; } = 90;

    public int SecurityEventDays { get; set; } = 400;

    public int LegalAcceptanceDays { get; set; } = 2555;

    public int PreciseLocationHours { get; set; } = 24;

    public int PendingAccountDays { get; set; } = 30;

    public int DisabledAccountDays { get; set; } = 365;

    public int AccountErasureGraceDays { get; set; } = 30;

    public int WorkerIntervalSeconds { get; set; } = 3600;

    public int WorkerMaxBatchesPerCycle { get; set; } = 10;

    public int SessionBatchSize { get; set; } = 500;

    public int RequestLogBatchSize { get; set; } = 2000;

    public int NonceBatchSize { get; set; } = 500;

    public int PreciseLocationBatchSize { get; set; } = 500;

    public int AccountBatchSize { get; set; } = 100;

    public int SecurityEventBatchSize { get; set; } = 500;

    public int LegalAcceptanceBatchSize { get; set; } = 500;
}

public sealed class RequestPipelineOptions
{
    public long BodyLimitBytes { get; set; } = 1_048_576;

    public int RequestLogQueueCapacity { get; set; } = 2048;

    public int RequestLogShutdownTimeoutSeconds { get; set; } = 5;

    public int RetentionShutdownTimeoutSeconds { get; set; } = 10;
}

public sealed class DevelopmentAuthenticationOptions
{
    public bool Enabled { get; set; }

    public string? Secret { get; set; }
}
