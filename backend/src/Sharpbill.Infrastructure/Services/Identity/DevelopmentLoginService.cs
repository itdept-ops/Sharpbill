using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed class DevelopmentLoginService : IDevelopmentLoginService
{
    private const string AdministratorRole = "admin";
    private const string DefaultRole = "user";

    private readonly IIdentityRepository _identityRepository;
    private readonly IUserRepository _userRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly SessionService _sessionService;
    private readonly ILegalService _legalService;
    private readonly IValidator<DevLoginRequest> _devRequestValidator;
    private readonly IClock _clock;
    private readonly AuthenticationPolicy _policy;
    private readonly AuthenticationAdmissionService _admission;
    private readonly AuthenticationAuditService _audit;
    private readonly SharpbillOptions _options;
    private readonly MySqlTransientRetryExecutor _retryExecutor;

    public DevelopmentLoginService(
        IIdentityRepository identityRepository,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        SessionService sessionService,
        ILegalService legalService,
        IValidator<DevLoginRequest> devRequestValidator,
        IClock clock,
        AuthenticationPolicy policy,
        AuthenticationAdmissionService admission,
        AuthenticationAuditService audit,
        IOptions<SharpbillOptions> options,
        MySqlTransientRetryExecutor? retryExecutor = null)
    {
        _identityRepository = identityRepository ??
            throw new ArgumentNullException(nameof(identityRepository));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
        _legalService = legalService ?? throw new ArgumentNullException(nameof(legalService));
        _devRequestValidator = devRequestValidator ??
            throw new ArgumentNullException(nameof(devRequestValidator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _retryExecutor = retryExecutor ?? MySqlTransientRetryExecutor.Default;
    }

    public async Task<AuthenticatedSession> LoginAsync(
        DevLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        if (!_policy.DevelopmentAuthenticationEnabled())
        {
            throw ApiException.NotFound("Not found");
        }

        _devRequestValidator.Validate(request).ThrowIfInvalid();
        _legalService.RequireCurrentAcceptance(request.LegalAccepted, request.LegalBundleVersion);
        return await _retryExecutor.ExecuteAsync(
            "auth.dev_login",
            async _ =>
            {
                await _unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    DateTime now = _clock.UtcNow;
                    string email = request.Email.Trim().ToLowerInvariant();
                    UserIdentity? devIdentity = await _identityRepository.FindAsync(
                        "dev",
                        string.Empty,
                        email,
                        false,
                        cancellationToken).ConfigureAwait(false);
                    User? user = devIdentity is null
                        ? await _userRepository.FindByEmailForAuthenticationAsync(
                            email,
                            cancellationToken).ConfigureAwait(false)
                        : await _userRepository.FindForAuthenticationAsync(
                            devIdentity.UserId,
                            cancellationToken).ConfigureAwait(false);
                    if (devIdentity is not null && user is null)
                    {
                        throw ApiException.Forbidden(
                            "ACCOUNT_DISABLED",
                            "This account is unavailable");
                    }

                    if (user is null)
                    {
                        string roleName = request.Role ??
                            (_options.IdentityProviders.DevelopmentAdminEmails.Contains(email)
                                ? AdministratorRole
                                : DefaultRole);
                        Role role = await _admission.FindRoleOrDefaultAsync(
                            roleName,
                            cancellationToken).ConfigureAwait(false);
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
                        int userId = await _userRepository.AddAsync(
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
                        int identityId = await _identityRepository.AddAsync(
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
                        AuthenticationPolicy.RequireAuthenticatable(user);
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
                            int identityId = await _identityRepository.AddAsync(
                                identity,
                                cancellationToken).ConfigureAwait(false);
                            user = user with
                            {
                                Identities = [.. user.Identities, identity with { Id = identityId }],
                            };
                        }

                        user = user with { LastLoginAt = now, LastSeenAt = now, UpdatedAt = now };
                        await _userRepository.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
                    }

                    await _audit.AddLoginSuccessEventAsync(
                        user.Id,
                        "dev",
                        context,
                        now,
                        cancellationToken).ConfigureAwait(false);
                    SessionToken token = await _sessionService.StageStartAsync(
                        user.Id,
                        request.LegalAccepted,
                        request.LegalBundleVersion,
                        context,
                        cancellationToken).ConfigureAwait(false);
                    await _unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new AuthenticatedSession(
                        IdentityUserMapper.ToResponse(user, online: true),
                        token);
                }
                catch
                {
                    await _unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}
