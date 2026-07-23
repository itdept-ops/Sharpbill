using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Users;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class DevelopmentAuthController(
    IDevelopmentAuthService developmentAuthService,
    IOptions<SharpbillOptions> options) : ControllerBase
{
    private readonly SharpbillOptions _options = options.Value;

    [HttpPost("dev")]
    public async Task<ActionResult<UserResponse>> LoginAsync(
        DevLoginRequest request,
        [FromHeader(Name = "X-Dev-Auth-Secret")] string? suppliedSecret,
        CancellationToken cancellationToken)
    {
        AuthenticatedSession authenticated = await developmentAuthService.LoginAsync(
            request,
            suppliedSecret,
            cancellationToken).ConfigureAwait(false);
        Response.Cookies.Append(CookieName, authenticated.Session.Value, Cookie(authenticated.Session));
        return Ok(authenticated.User);
    }

    [HttpGet("dev/roles")]
    public async Task<ActionResult<IReadOnlyList<string>>> ListRolesAsync(
        [FromHeader(Name = "X-Dev-Auth-Secret")] string? suppliedSecret,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> roles = await developmentAuthService.ListRolesAsync(
            suppliedSecret,
            cancellationToken).ConfigureAwait(false);
        return Ok(roles);
    }

    private string CookieName => _options.Session.LocalCookieName;

    private CookieOptions Cookie(SessionToken token) => new()
    {
        HttpOnly = true,
        Secure = _options.Session.SecureCookie,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
        MaxAge = token.ExpiresAt - token.IssuedAt,
        Expires = new DateTimeOffset(DateTime.SpecifyKind(token.ExpiresAt, DateTimeKind.Utc)),
    };
}
