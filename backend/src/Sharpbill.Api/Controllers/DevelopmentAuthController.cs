using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Users;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public sealed class DevelopmentAuthController(
    IAuthService authService,
    IRoleRepository roles,
    IOptions<SharpbillOptions> options) : ControllerBase
{
    private readonly SharpbillOptions _options = options.Value;

    [HttpPost("dev")]
    public async Task<ActionResult<UserResponse>> LoginAsync(
        DevLoginRequest request,
        [FromHeader(Name = "X-Dev-Auth-Secret")] string? suppliedSecret,
        CancellationToken cancellationToken)
    {
        RequireSecret(suppliedSecret);
        var context = new RequestContext
        {
            RequestId = HttpContext.TraceIdentifier,
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : null,
        };
        AuthenticatedSession authenticated = await authService.DevLoginAsync(
            request,
            context,
            cancellationToken).ConfigureAwait(false);
        Response.Cookies.Append(CookieName, authenticated.Session.Value, Cookie(authenticated.Session));
        return Ok(authenticated.User);
    }

    [HttpGet("dev/roles")]
    public async Task<ActionResult<IReadOnlyList<string>>> ListRolesAsync(
        [FromHeader(Name = "X-Dev-Auth-Secret")] string? suppliedSecret,
        CancellationToken cancellationToken)
    {
        RequireSecret(suppliedSecret);
        var rows = await roles.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(rows.OrderBy(static role => role.Id).Select(static role => role.Name).ToArray());
    }

    private void RequireSecret(string? supplied)
    {
        string? expected = _options.DevelopmentAuthentication.Secret;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(supplied) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(supplied)))
        {
            throw ApiException.NotFound("Not found");
        }
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
