using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class AuthService : IAuthService
{
    private readonly IAuthConfigurationService _configuration;
    private readonly IExternalLoginService _externalLogin;
    private readonly IDevelopmentLoginService _developmentLogin;
    private readonly IAuthAccountService _account;
    private readonly IAuthSessionOperationsService _sessions;

    public AuthService(
        IAuthConfigurationService configuration,
        IExternalLoginService externalLogin,
        IDevelopmentLoginService developmentLogin,
        IAuthAccountService account,
        IAuthSessionOperationsService sessions)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _externalLogin = externalLogin ?? throw new ArgumentNullException(nameof(externalLogin));
        _developmentLogin = developmentLogin ??
            throw new ArgumentNullException(nameof(developmentLogin));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public Task<AuthConfigResponse> GetConfigurationAsync(CancellationToken cancellationToken) =>
        _configuration.GetConfigurationAsync(cancellationToken);

    public Task<AuthenticatedSession> LoginAsync(
        ProviderContract provider,
        TokenLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken) =>
        _externalLogin.LoginAsync(provider, request, context, cancellationToken);

    public Task<AuthenticatedSession> DevLoginAsync(
        DevLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken) =>
        _developmentLogin.LoginAsync(request, context, cancellationToken);

    public Task LogoutAsync(
        RequestContext context,
        CancellationToken cancellationToken) =>
        _sessions.LogoutAsync(context, cancellationToken);

    public Task<UserResponse> GetCurrentUserAsync(
        int userId,
        CancellationToken cancellationToken) =>
        _account.GetCurrentUserAsync(userId, cancellationToken);
}
