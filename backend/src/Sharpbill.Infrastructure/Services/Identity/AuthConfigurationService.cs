using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Identity;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed class AuthConfigurationService : IAuthConfigurationService
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly AuthenticationPolicy _policy;

    public AuthConfigurationService(
        ISettingsRepository settingsRepository,
        AuthenticationPolicy policy)
    {
        _settingsRepository = settingsRepository ??
            throw new ArgumentNullException(nameof(settingsRepository));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<AuthConfigResponse> GetConfigurationAsync(
        CancellationToken cancellationToken)
    {
        SiteSettings? site = await _settingsRepository.GetAsync(
            false,
            cancellationToken).ConfigureAwait(false);
        bool google = _policy.ProviderEnabled(site, ProviderContract.Google);
        bool microsoft = _policy.ProviderEnabled(site, ProviderContract.Microsoft);
        return new AuthConfigResponse
        {
            Google = google,
            Microsoft = microsoft,
            GoogleClientId = google ? _policy.ClientId(ProviderContract.Google) : null,
            MicrosoftClientId = microsoft ? _policy.ClientId(ProviderContract.Microsoft) : null,
            Dev = _policy.DevelopmentAuthenticationEnabled(),
            Calm = site?.CalmMode == true,
        };
    }
}
