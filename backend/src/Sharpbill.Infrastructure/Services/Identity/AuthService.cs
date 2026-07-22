using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed partial class AuthService(
    IEnumerable<IIdentityTokenVerifier> tokenVerifiers,
    IIdentityRepository identityRepository,
    IUserRepository userRepository,
    IRoleRepository roleRepository,
    ISettingsRepository settingsRepository,
    ISessionRepository sessionRepository,
    ISecurityEventRepository securityEventRepository,
    IUnitOfWork unitOfWork,
    SessionService sessionService,
    ILegalService legalService,
    IValidator<TokenLoginRequest> tokenRequestValidator,
    IValidator<DevLoginRequest> devRequestValidator,
    IClock clock,
    IOptions<SharpbillOptions> options,
    ILogger<AuthService> logger,
    MySqlTransientRetryExecutor? retryExecutor = null) : IAuthService
{
    private const string AdministratorRole = "admin";
    private const string DefaultRole = "user";
    private readonly Dictionary<ProviderContract, IIdentityTokenVerifier> _tokenVerifiers =
        tokenVerifiers.ToDictionary(static verifier => verifier.Provider);
    private readonly SharpbillOptions _options = options.Value;
    private readonly MySqlTransientRetryExecutor _retryExecutor =
        retryExecutor ?? MySqlTransientRetryExecutor.Default;

    public async Task<AuthConfigResponse> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        SiteSettings? site = await settingsRepository.GetAsync(
            false,
            cancellationToken).ConfigureAwait(false);
        bool google = !string.IsNullOrWhiteSpace(_options.IdentityProviders.GoogleClientId) &&
            site?.AllowGoogle == true;
        bool microsoft = !string.IsNullOrWhiteSpace(_options.IdentityProviders.MicrosoftClientId) &&
            site?.AllowMicrosoft == true;
        return new AuthConfigResponse
        {
            Google = google,
            Microsoft = microsoft,
            GoogleClientId = google ? _options.IdentityProviders.GoogleClientId : null,
            MicrosoftClientId = microsoft ? _options.IdentityProviders.MicrosoftClientId : null,
            Dev = DevelopmentAuthenticationEnabled(),
            Calm = site?.CalmMode == true,
        };
    }

    public async Task<AuthenticatedSession> LoginAsync(
        ProviderContract provider,
        TokenLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        tokenRequestValidator.Validate(request).ThrowIfInvalid();
        legalService.RequireCurrentAcceptance(request.LegalAccepted, request.LegalBundleVersion);
        if (provider is not (ProviderContract.Google or ProviderContract.Microsoft) ||
            !_tokenVerifiers.TryGetValue(provider, out IIdentityTokenVerifier? verifier))
        {
            throw ApiException.BadRequest("INVALID_PROVIDER", "Unsupported identity provider");
        }

        string providerName = ProviderName(provider);
        SiteSettings? initialSettings = await settingsRepository.GetAsync(
            false,
            cancellationToken).ConfigureAwait(false);
        if (!ProviderEnabled(initialSettings, provider))
        {
            await AuditLoginFailureAsync(
                providerName,
                "PROVIDER_DISABLED",
                SecurityEventOutcome.Denied,
                context).ConfigureAwait(false);
            throw ProviderDisabled(provider);
        }

        VerifiedIdentity identity;
        try
        {
            string expectedNonce = OidcTokenClaims.ReadUnverifiedNonce(request.IdToken);
            identity = await verifier.VerifyAsync(
                request.IdToken,
                expectedNonce,
                cancellationToken).ConfigureAwait(false);
        }
        catch (IdentityProviderUnavailableException)
        {
            await AuditLoginFailureAsync(
                providerName,
                "PROVIDER_UNAVAILABLE",
                SecurityEventOutcome.Failure,
                context).ConfigureAwait(false);
            throw new ApiException(
                503,
                "PROVIDER_UNAVAILABLE",
                $"{ProviderDisplayName(provider)} sign-in is temporarily unavailable");
        }
        catch (IdentityTokenException)
        {
            await AuditLoginFailureAsync(
                providerName,
                "INVALID_TOKEN",
                SecurityEventOutcome.Denied,
                context).ConfigureAwait(false);
            throw ApiException.Unauthorized(
                "INVALID_TOKEN",
                $"Invalid {ProviderDisplayName(provider)} token");
        }

        try
        {
            return await CompleteProviderLoginAsync(
                identity,
                request,
                context,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException exception)
        {
            await AuditLoginFailureAsync(
                providerName,
                exception.Code,
                SecurityEventOutcome.Denied,
                context).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AuthenticatedSession> DevLoginAsync(
        DevLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        if (!DevelopmentAuthenticationEnabled())
        {
            throw ApiException.NotFound("Not found");
        }

        devRequestValidator.Validate(request).ThrowIfInvalid();
        legalService.RequireCurrentAcceptance(request.LegalAccepted, request.LegalBundleVersion);
        return await _retryExecutor.ExecuteAsync(
            "auth.dev_login",
            async _ =>
            {
                await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    DateTime now = clock.UtcNow;
                    string email = request.Email.Trim().ToLowerInvariant();
                    UserIdentity? devIdentity = await identityRepository.FindAsync(
                        "dev",
                        string.Empty,
                        email,
                        false,
                        cancellationToken).ConfigureAwait(false);
                    User? user = devIdentity is null
                        ? await userRepository.FindByEmailForAuthenticationAsync(
                            email,
                            cancellationToken).ConfigureAwait(false)
                        : await userRepository.FindForAuthenticationAsync(
                            devIdentity.UserId,
                            cancellationToken).ConfigureAwait(false);
                    if (devIdentity is not null && user is null)
                    {
                        throw ApiException.Forbidden("ACCOUNT_DISABLED", "This account is unavailable");
                    }

                    if (user is null)
                    {
                        string roleName = request.Role ??
                            (_options.IdentityProviders.DevelopmentAdminEmails.Contains(email)
                                ? AdministratorRole
                                : DefaultRole);
                        Role role = await FindRoleOrDefaultAsync(roleName, cancellationToken).ConfigureAwait(false);
                        var newUser = new User
                        {
                            Id = 0,
                            Email = email,
                            DisplayName = string.IsNullOrEmpty(request.DisplayName)
                                ? email.Split('@')[0]
                                : request.DisplayName,
                            RoleId = role.Id,
                            RoleName = role.Name,
                            IsActive = true,
                            IsApproved = true,
                            AccessVersion = 1,
                            LastLoginAt = now,
                            LastSeenAt = now,
                            CreatedAt = now,
                            UpdatedAt = now,
                            RolePermissionKeys = role.PermissionKeys,
                        };
                        int userId = await userRepository.AddAsync(
                            newUser,
                            cancellationToken).ConfigureAwait(false);
                        var identity = new UserIdentity
                        {
                            Id = 0,
                            UserId = userId,
                            Provider = IdentityProvider.Dev,
                            ProviderNamespace = string.Empty,
                            ProviderSubject = email,
                            CreatedAt = now,
                            UpdatedAt = now,
                        };
                        int identityId = await identityRepository.AddAsync(
                            identity,
                            cancellationToken).ConfigureAwait(false);
                        user = newUser with
                        {
                            Id = userId,
                            Identities = [identity with { Id = identityId }],
                        };
                    }
                    else
                    {
                        RequireAuthenticatable(user);
                        if (devIdentity is null)
                        {
                            var identity = new UserIdentity
                            {
                                Id = 0,
                                UserId = user.Id,
                                Provider = IdentityProvider.Dev,
                                ProviderNamespace = string.Empty,
                                ProviderSubject = email,
                                CreatedAt = now,
                                UpdatedAt = now,
                            };
                            int identityId = await identityRepository.AddAsync(
                                identity,
                                cancellationToken).ConfigureAwait(false);
                            user = user with
                            {
                                Identities = [.. user.Identities, identity with { Id = identityId }],
                            };
                        }

                        user = user with { LastLoginAt = now, LastSeenAt = now, UpdatedAt = now };
                        await userRepository.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
                    }

                    await AddLoginSuccessEventAsync(
                        user.Id,
                        "dev",
                        context,
                        now,
                        cancellationToken).ConfigureAwait(false);
                    SessionToken token = await sessionService.StageStartAsync(
                        user.Id,
                        request.LegalAccepted,
                        request.LegalBundleVersion,
                        context,
                        cancellationToken).ConfigureAwait(false);
                    await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new AuthenticatedSession(
                        IdentityUserMapper.ToResponse(user, online: true),
                        token);
                }
                catch
                {
                    await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(RequestContext context, CancellationToken cancellationToken)
    {
        if (context.SessionJti is not { } jti)
        {
            return;
        }

        await _retryExecutor.ExecuteAsync(
            "auth.logout",
            async _ =>
            {
                await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    UserSession? session = await sessionRepository.FindByJtiAsync(
                        jti,
                        true,
                        cancellationToken).ConfigureAwait(false);
                    if (context.SessionUserId.HasValue && session?.UserId != context.SessionUserId.Value)
                    {
                        session = null;
                    }

                    int? auditUserId = session?.UserId ?? context.SessionUserId;
                    if (!auditUserId.HasValue)
                    {
                        await unitOfWork.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    DateTime now = clock.UtcNow;
                    bool revoked = session?.RevokedAt is null && session is not null;
                    if (revoked)
                    {
                        await sessionRepository.RevokeAsync(session!.Id, now, cancellationToken).ConfigureAwait(false);
                    }

                    var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["session_revoked"] = revoked,
                    };
                    SecurityEvent securityEvent = IdentitySecurityEventFactory.Create(
                        "auth.logout",
                        SecurityEventOutcome.Success,
                        SecurityEventSeverity.Info,
                        context,
                        now,
                        _options.Retention.SecurityEventDays,
                        auditUserId.Value,
                        "user",
                        auditUserId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        metadata);
                    await securityEventRepository.AddWithPendingDeliveryAsync(
                        securityEvent,
                        cancellationToken).ConfigureAwait(false);
                    await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserResponse> GetCurrentUserAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        User user = await userRepository.FindAsync(
            userId,
            false,
            cancellationToken).ConfigureAwait(false)
            ?? throw ApiException.Unauthorized("INVALID_SESSION", "Session invalid or expired");
        if (!IsAuthenticatable(user))
        {
            throw ApiException.Unauthorized("INVALID_SESSION", "Session invalid or expired");
        }

        return IdentityUserMapper.ToResponse(user, online: true);
    }

    private async Task<AuthenticatedSession> CompleteProviderLoginAsync(
        VerifiedIdentity identity,
        TokenLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        return await _retryExecutor.ExecuteAsync(
            "auth.provider_login",
            async _ =>
            {
                for (int attempt = 0; ; attempt++)
                {
                    await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        SiteSettings site = await settingsRepository.GetForShareAsync(
                            cancellationToken).ConfigureAwait(false)
                            ?? throw new ApiException(500, "INTERNAL_ERROR", "Site settings row is missing");
                        if (!ProviderEnabled(site, identity.Provider))
                        {
                            throw ProviderDisabled(identity.Provider);
                        }

                        ProvisionedUser provisioned = await FindOrProvisionAsync(
                            site,
                            identity,
                            cancellationToken).ConfigureAwait(false);
                        if (!provisioned.User.IsApproved)
                        {
                            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                            throw ApiException.Forbidden(
                                "PENDING_APPROVAL",
                                provisioned.WasCreated
                                    ? "Your account was created and is awaiting approval"
                                    : "Your account is awaiting administrator approval");
                        }

                        RequireAuthenticatable(provisioned.User);
                        DateTime now = clock.UtcNow;
                        await AddLoginSuccessEventAsync(
                            provisioned.User.Id,
                            ProviderName(identity.Provider),
                            context,
                            now,
                            cancellationToken).ConfigureAwait(false);
                        SessionToken token = await sessionService.StageStartAsync(
                            provisioned.User.Id,
                            request.LegalAccepted,
                            request.LegalBundleVersion,
                            context,
                            cancellationToken).ConfigureAwait(false);
                        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return new AuthenticatedSession(
                            IdentityUserMapper.ToResponse(provisioned.User, online: true),
                            token);
                    }
                    catch (MySqlException exception) when (exception.Number == 1062 && attempt == 0)
                    {
                        await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProvisionedUser> FindOrProvisionAsync(
        SiteSettings site,
        VerifiedIdentity identity,
        CancellationToken cancellationToken)
    {
        DateTime now = clock.UtcNow;
        string providerName = ProviderName(identity.Provider);
        string identityNamespace = IdentityNamespace(identity);
        UserIdentity? storedIdentity = await identityRepository.FindAsync(
            providerName,
            identityNamespace,
            identity.Subject,
            false,
            cancellationToken).ConfigureAwait(false);
        if (storedIdentity is not null)
        {
            User user = await userRepository.FindForAuthenticationAsync(
                storedIdentity.UserId,
                cancellationToken).ConfigureAwait(false)
                ?? throw ApiException.Forbidden("ACCOUNT_DISABLED", "This account is unavailable");
            RequireAuthenticatable(user);
            var updatedIdentity = storedIdentity with
            {
                ProviderTenantId = identity.TenantId,
                ProviderHostedDomain = identity.HostedDomain,
                UpdatedAt = now,
            };
            await identityRepository.UpdateEvidenceAsync(
                updatedIdentity,
                cancellationToken).ConfigureAwait(false);
            user = user with
            {
                LastLoginAt = now,
                LastSeenAt = now,
                UpdatedAt = now,
                Identities = user.Identities
                    .Select(item => item.Id == updatedIdentity.Id ? updatedIdentity : item)
                    .ToArray(),
            };
            await userRepository.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
            return new ProvisionedUser(user, false);
        }

        bool administratorBootstrap = IsAdministratorBootstrap(identity);
        if (site.SignupMode == SignupMode.Closed && !administratorBootstrap)
        {
            throw ApiException.Forbidden("SIGNUP_CLOSED", "Sign-ups are currently closed");
        }

        Role role = administratorBootstrap
            ? await FindRoleOrDefaultAsync(AdministratorRole, cancellationToken).ConfigureAwait(false)
            : await FindDefaultRoleAsync(site.DefaultRoleId, cancellationToken).ConfigureAwait(false);
        bool approved = administratorBootstrap || site.SignupMode == SignupMode.Open;
        var newUser = new User
        {
            Id = 0,
            Email = identity.Email.ToLowerInvariant(),
            DisplayName = identity.DisplayName,
            RoleId = role.Id,
            RoleName = role.Name,
            IsActive = true,
            IsApproved = approved,
            AccessVersion = 1,
            LastLoginAt = now,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            RolePermissionKeys = role.PermissionKeys,
        };
        int userId = await userRepository.AddAsync(newUser, cancellationToken).ConfigureAwait(false);
        var newIdentity = new UserIdentity
        {
            Id = 0,
            UserId = userId,
            Provider = ToDomainProvider(identity.Provider),
            ProviderNamespace = identityNamespace,
            ProviderSubject = identity.Subject,
            ProviderTenantId = identity.TenantId,
            ProviderHostedDomain = identity.HostedDomain,
            CreatedAt = now,
            UpdatedAt = now,
        };
        int identityId = await identityRepository.AddAsync(
            newIdentity,
            cancellationToken).ConfigureAwait(false);
        return new ProvisionedUser(
            newUser with
            {
                Id = userId,
                Identities = [newIdentity with { Id = identityId }],
            },
            true);
    }

    private async Task<Role> FindDefaultRoleAsync(int defaultRoleId, CancellationToken cancellationToken)
    {
        Role? role = await roleRepository.FindAsync(
            defaultRoleId,
            false,
            cancellationToken).ConfigureAwait(false);
        return role ?? await FindRoleOrDefaultAsync(DefaultRole, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Role> FindRoleOrDefaultAsync(string roleName, CancellationToken cancellationToken)
    {
        Role? role = await roleRepository.FindByNameAsync(
            roleName,
            false,
            cancellationToken).ConfigureAwait(false);
        role ??= await roleRepository.FindByNameAsync(
            DefaultRole,
            false,
            cancellationToken).ConfigureAwait(false);
        return role ?? throw new ApiException(500, "INTERNAL_ERROR", "Default role is missing");
    }

    private async Task AddLoginSuccessEventAsync(
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
        _ = await securityEventRepository.AddWithPendingDeliveryAsync(
            securityEvent,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AuditLoginFailureAsync(
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
            await unitOfWork.RollbackAsync(auditTimeout.Token).ConfigureAwait(false);
            await _retryExecutor.ExecuteTransactionAsync(
                unitOfWork,
                "auth.audit_failure",
                async token =>
                {
                    DateTime now = clock.UtcNow;
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
                    await securityEventRepository.AddWithPendingDeliveryAsync(
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
                await unitOfWork.RollbackAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                persistenceException = new AggregateException(exception, cleanupException);
            }

            LogAuditPersistenceFailure(logger, provider, outcome.ToString(), persistenceException);
        }
    }

    private bool IsAdministratorBootstrap(VerifiedIdentity identity) => identity.Provider switch
    {
        ProviderContract.Google =>
            _options.IdentityProviders.GoogleAdminSubjects.Contains(identity.Subject) ||
            (_options.IsLocal && _options.IdentityProviders.DevelopmentAdminEmails.Contains(identity.Email)),
        ProviderContract.Microsoft =>
            !string.IsNullOrWhiteSpace(_options.IdentityProviders.MicrosoftAdminTenantId) &&
            string.Equals(
                identity.TenantId,
                _options.IdentityProviders.MicrosoftAdminTenantId,
                StringComparison.OrdinalIgnoreCase) &&
            _options.IdentityProviders.MicrosoftAdminObjectIds.Contains(identity.Subject),
        _ => false,
    };

    private bool ProviderEnabled(SiteSettings? settings, ProviderContract provider) =>
        settings is not null && provider switch
        {
            ProviderContract.Google => settings.AllowGoogle &&
                !string.IsNullOrWhiteSpace(_options.IdentityProviders.GoogleClientId),
            ProviderContract.Microsoft => settings.AllowMicrosoft &&
                !string.IsNullOrWhiteSpace(_options.IdentityProviders.MicrosoftClientId),
            _ => false,
        };

    private bool DevelopmentAuthenticationEnabled() =>
        DevelopmentAuthenticationGuard.IsEnabled(_options);

    private static string IdentityNamespace(VerifiedIdentity identity)
    {
        if (identity.Provider != ProviderContract.Microsoft)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(identity.TenantId))
        {
            throw ApiException.Unauthorized(
                "INVALID_IDENTITY",
                "Microsoft identity is missing its tenant");
        }

        return identity.TenantId;
    }

    private static void RequireAuthenticatable(User user)
    {
        if (user.ErasedAt is not null)
        {
            throw ApiException.Forbidden("ACCOUNT_ERASED", "This account has been erased");
        }

        if (!user.IsApproved)
        {
            throw ApiException.Forbidden(
                "PENDING_APPROVAL",
                "Your account is awaiting administrator approval");
        }

        if (!user.IsActive)
        {
            throw ApiException.Forbidden("ACCOUNT_DISABLED", "This account has been deactivated");
        }
    }

    private static bool IsAuthenticatable(User user) =>
        user.ErasedAt is null && user.IsActive && user.IsApproved;

    private static ApiException ProviderDisabled(ProviderContract provider) =>
        ApiException.Forbidden(
            "PROVIDER_DISABLED",
            $"{ProviderDisplayName(provider)} sign-in is currently disabled");

    private static string ProviderName(ProviderContract provider) => provider switch
    {
        ProviderContract.Google => "google",
        ProviderContract.Microsoft => "microsoft",
        _ => "dev",
    };

    private static string ProviderDisplayName(ProviderContract provider) => provider switch
    {
        ProviderContract.Google => "Google",
        ProviderContract.Microsoft => "Microsoft",
        _ => "Development",
    };

    private static IdentityProvider ToDomainProvider(ProviderContract provider) => provider switch
    {
        ProviderContract.Google => IdentityProvider.Google,
        ProviderContract.Microsoft => IdentityProvider.Microsoft,
        _ => IdentityProvider.Dev,
    };

    private sealed record ProvisionedUser(User User, bool WasCreated);

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
