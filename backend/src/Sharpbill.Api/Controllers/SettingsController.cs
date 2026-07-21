using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Settings;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Api.Controllers;

[Route("api/admin/settings")]
[Authorize(Policy = PermissionKeys.SettingsManage)]
public sealed class SettingsController(ISettingsService settingsService) : SharpbillControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SiteSettingsResponse>> GetAsync(CancellationToken cancellationToken) =>
        Ok(await settingsService.GetAsync(ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpPut]
    public async Task<ActionResult<SiteSettingsResponse>> UpdateAsync(
        SiteSettingsUpdateRequest request,
        CancellationToken cancellationToken) =>
        Ok(await settingsService.UpdateAsync(ActorUserId, request, cancellationToken).ConfigureAwait(false));
}
