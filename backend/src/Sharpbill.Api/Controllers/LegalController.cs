using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Legal;

namespace Sharpbill.Api.Controllers;

[ApiController]
[Route("api/legal")]
[AllowAnonymous]
public sealed class LegalController(ILegalService legalService) : ControllerBase
{
    [HttpGet("manifest")]
    public ActionResult<LegalManifestResponse> Manifest() => Ok(legalService.GetManifest());
}
