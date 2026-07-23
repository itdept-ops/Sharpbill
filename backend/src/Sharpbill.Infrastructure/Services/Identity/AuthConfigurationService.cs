using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Auth;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed class AuthConfigurationService : IAuthConfigurationService
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly AuthenticationPolicy _policy;
    private readonly SharpbillOptions _options;

    public AuthConfigurationService(
        ISettingsRepository settingsRepository,
        AuthenticationPolicy policy,
        IOptions<SharpbillOptions> options)
    {
        _settingsRepository = settingsRepository ??
            throw new ArgumentNullException(nameof(settingsRepository));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<AuthConfigResponse> GetConfigurationAsync(
        CancellationToken cancellationToken)
    {
        SiteSettings? site = await _settingsRepository.GetAsync(
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
            Dev = _policy.DevelopmentAuthenticationEnabled(),
            Calm = site?.CalmMode == true,
        };
    }
}
