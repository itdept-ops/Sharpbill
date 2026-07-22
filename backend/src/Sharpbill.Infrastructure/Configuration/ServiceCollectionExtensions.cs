using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Application.Validation;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Contracts.Users;
using Sharpbill.Infrastructure.Database;
using Sharpbill.Infrastructure.Repositories;
using Sharpbill.Infrastructure.Runtime;
using Sharpbill.Infrastructure.Services.Business;
using Sharpbill.Infrastructure.Services.Identity;
using Sharpbill.Infrastructure.Services.Operations;

namespace Sharpbill.Infrastructure.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSharpbillConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IConfigureOptions<SharpbillOptions>>(
            new SharpbillOptionsSetup(configuration));
        services.AddSingleton<IValidateOptions<SharpbillOptions>, SharpbillOptionsValidator>();
        services.AddOptions<SharpbillOptions>().ValidateOnStart();
        return services;
    }

    public static IServiceCollection AddSharpbillRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClock, TimeProviderClock>();
        services.AddScoped<IRequestContextAccessor, RequestContextAccessor>();
        services.AddSingleton<IDatabaseConnectionFactory, MySqlConnectionFactory>();
        services.AddSingleton(DatabaseRetryTelemetry.Shared);
        services.AddSingleton<MySqlTransientRetryExecutor>();
        services.AddScoped<DatabaseSession>();
        services.AddScoped<IUnitOfWork>(static provider => provider.GetRequiredService<DatabaseSession>());
        services.AddMemoryCache();

        services.AddScoped<IIdentityRepository, IdentityRepository>();
        services.AddScoped<INonceRepository, NonceRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<ILegalAcceptanceRepository, LegalAcceptanceRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();
        services.AddScoped<ISecurityEventRepository, SecurityEventRepository>();
        services.AddScoped<IEventOutboxRepository, EventOutboxRepository>();
        services.AddScoped<IRequestLogRepository, RequestLogRepository>();
        services.AddScoped<IPresenceRepository, PresenceRepository>();
        services.AddScoped<IDashboardRepository, DashboardRepository>();
        services.AddScoped<IHealthRepository, HealthRepository>();
        services.AddScoped<IRetentionRepository, RetentionRepository>();

        services.AddSingleton<IValidator<TokenLoginRequest>, TokenLoginRequestValidator>();
        services.AddSingleton<IValidator<DevLoginRequest>, DevLoginRequestValidator>();
        services.AddSingleton<IValidator<LocationUpdateRequest>, LocationUpdateRequestValidator>();
        services.AddSingleton<IValidator<PermissionCreateRequest>, PermissionCreateRequestValidator>();
        services.AddSingleton<IValidator<RoleCreateRequest>, RoleCreateRequestValidator>();
        services.AddSingleton<IValidator<RoleUpdateRequest>, RoleUpdateRequestValidator>();
        services.AddSingleton<IValidator<ProfileUpdateRequest>, ProfileUpdateRequestValidator>();
        services.AddSingleton<IValidator<BulkActionRequest>, BulkActionRequestValidator>();
        services.AddSingleton<IValidator<RetentionHoldUpdateRequest>, RetentionHoldUpdateRequestValidator>();
        services.AddSingleton<IValidator<UserQuery>, UserQueryValidator>();
        services.AddSingleton<IValidator<RequestLogQuery>, RequestLogQueryValidator>();
        services.AddSingleton<IValidator<SecurityEventQuery>, SecurityEventQueryValidator>();
        services.AddSingleton<IValidator<RetentionPolicyOptions>, RetentionPolicyValidator>();

        services.AddSingleton<IGeoService, GeoService>();
        services.AddSingleton<RetentionTelemetry>();
        services.AddScoped<ISecurityEventService, SecurityEventService>();
        services.AddScoped<IEventOutboxService, EventOutboxService>();
        services.AddScoped<IRequestLogService, RequestLogService>();
        services.AddScoped<IPresenceService, PresenceService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IHealthService, HealthService>();
        services.AddScoped<IRetentionService, RetentionService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IPrivacyService, PrivacyService>();

        services.AddSingleton<ProviderVerificationRuntime>();
        services.AddSingleton<ProviderDocumentClient>();
        services.AddSingleton<GoogleSigningKeyStore>();
        services.AddSingleton<MicrosoftSigningKeyStore>();
        services.AddSingleton<SessionJwtIssuer>();
        services.AddScoped<INonceService, NonceService>();
        services.AddScoped<ILegalService, LegalService>();
        services.AddScoped<SessionService>();
        services.AddScoped<ISessionService>(static provider => provider.GetRequiredService<SessionService>());
        services.AddScoped<IIdentityTokenVerifier, GoogleIdentityTokenVerifier>();
        services.AddScoped<IIdentityTokenVerifier, MicrosoftIdentityTokenVerifier>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddHttpClient(IdentityProviderHttpClientNames.SigningKeys)
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                IdentityProviderOptions identity = provider
                    .GetRequiredService<IOptions<SharpbillOptions>>().Value.IdentityProviders;
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    ConnectTimeout = TimeSpan.FromSeconds(identity.ConnectTimeoutSeconds),
                    AutomaticDecompression = System.Net.DecompressionMethods.None,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                };
            });
        return services;
    }
}
