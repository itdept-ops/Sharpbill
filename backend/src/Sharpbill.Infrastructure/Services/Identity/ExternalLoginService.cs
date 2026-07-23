using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Identity;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed class ExternalLoginService : IExternalLoginService
{
    private readonly Dictionary<ProviderContract, IIdentityTokenVerifier> _tokenVerifiers;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SessionService _sessionService;
    private readonly ILegalService _legalService;
    private readonly IValidator<TokenLoginRequest> _tokenRequestValidator;
    private readonly IClock _clock;
    private readonly AuthenticationPolicy _policy;
    private readonly AuthenticationAdmissionService _admission;
    private readonly AuthenticationAuditService _audit;
    private readonly MySqlTransientRetryExecutor _retryExecutor;

    public ExternalLoginService(
        IEnumerable<IIdentityTokenVerifier> tokenVerifiers,
        ISettingsRepository settingsRepository,
        IUnitOfWork unitOfWork,
        SessionService sessionService,
        ILegalService legalService,
        IValidator<TokenLoginRequest> tokenRequestValidator,
        IClock clock,
        AuthenticationPolicy policy,
        AuthenticationAdmissionService admission,
        AuthenticationAuditService audit,
        MySqlTransientRetryExecutor? retryExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(tokenVerifiers);
        _tokenVerifiers = tokenVerifiers.ToDictionary(static verifier => verifier.Provider);
        _settingsRepository = settingsRepository ??
            throw new ArgumentNullException(nameof(settingsRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _legalService = legalService ?? throw new ArgumentNullException(nameof(legalService));
        _tokenRequestValidator = tokenRequestValidator ??
            throw new ArgumentNullException(nameof(tokenRequestValidator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _retryExecutor = retryExecutor ?? MySqlTransientRetryExecutor.Default;
    }

    public async Task<AuthenticatedSession> LoginAsync(
        ProviderContract provider,
        TokenLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        _tokenRequestValidator.Validate(request).ThrowIfInvalid();
        _legalService.RequireCurrentAcceptance(request.LegalAccepted, request.LegalBundleVersion);
        if (provider is not (ProviderContract.Google or ProviderContract.Microsoft) ||
            !_tokenVerifiers.TryGetValue(provider, out IIdentityTokenVerifier? verifier))
        {
            throw ApiException.BadRequest("INVALID_PROVIDER", "Unsupported identity provider");
        }

        string providerName = AuthenticationPolicy.ProviderName(provider);
        SiteSettings? initialSettings = await _settingsRepository.GetAsync(
            false,
            cancellationToken).ConfigureAwait(false);
        if (!_policy.ProviderEnabled(initialSettings, provider))
        {
            await _audit.AuditLoginFailureAsync(
                providerName,
                "PROVIDER_DISABLED",
                SecurityEventOutcome.Denied,
                context).ConfigureAwait(false);
            throw AuthenticationPolicy.ProviderDisabled(provider);
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
            await _audit.AuditLoginFailureAsync(
                providerName,
                "PROVIDER_UNAVAILABLE",
                SecurityEventOutcome.Failure,
                context).ConfigureAwait(false);
            throw new ApiException(
                503,
                "PROVIDER_UNAVAILABLE",
                $"{AuthenticationPolicy.ProviderDisplayName(provider)} sign-in is temporarily unavailable");
        }
        catch (IdentityTokenException)
        {
            await _audit.AuditLoginFailureAsync(
                providerName,
                "INVALID_TOKEN",
                SecurityEventOutcome.Denied,
                context).ConfigureAwait(false);
            throw ApiException.Unauthorized(
                "INVALID_TOKEN",
                $"Invalid {AuthenticationPolicy.ProviderDisplayName(provider)} token");
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
            await _audit.AuditLoginFailureAsync(
                providerName,
                exception.Code,
                SecurityEventOutcome.Denied,
                context).ConfigureAwait(false);
            throw;
        }
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
                    await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        SiteSettings site = await _settingsRepository.GetForShareAsync(
                            cancellationToken).ConfigureAwait(false)
                            ?? throw new ApiException(
                                500,
                                "INTERNAL_ERROR",
                                "Site settings row is missing");
                        if (!_policy.ProviderEnabled(site, identity.Provider))
                        {
                            throw AuthenticationPolicy.ProviderDisabled(identity.Provider);
                        }

                        AuthenticationAdmissionResult provisioned =
                            await _admission.FindOrProvisionAsync(
                                site,
                                identity,
                                cancellationToken).ConfigureAwait(false);
                        if (!provisioned.User.IsApproved)
                        {
                            await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                            throw ApiException.Forbidden(
                                "PENDING_APPROVAL",
                                provisioned.WasCreated
                                    ? "Your account was created and is awaiting approval"
                                    : "Your account is awaiting administrator approval");
                        }

                        AuthenticationPolicy.RequireAuthenticatable(provisioned.User);
                        DateTime now = _clock.UtcNow;
                        await _audit.AddLoginSuccessEventAsync(
                            provisioned.User.Id,
                            AuthenticationPolicy.ProviderName(identity.Provider),
                            context,
                            now,
                            cancellationToken).ConfigureAwait(false);
                        SessionToken token = await _sessionService.StageStartAsync(
                            provisioned.User.Id,
                            request.LegalAccepted,
                            request.LegalBundleVersion,
                            context,
                            cancellationToken).ConfigureAwait(false);
                        await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return new AuthenticatedSession(
                            IdentityUserMapper.ToResponse(provisioned.User, online: true),
                            token);
                    }
                    catch (MySqlException exception) when (exception.Number == 1062 && attempt == 0)
                    {
                        await _unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        await _unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}
