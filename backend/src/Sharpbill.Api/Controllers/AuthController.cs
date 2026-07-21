using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Controllers;

[Route("api/auth")]
public sealed class AuthController(
    IAuthService authService,
    INonceService nonceService,
    ISessionService sessionService,
    IUserService userService,
    IRequestContextAccessor requestContextAccessor,
    IOptions<SharpbillOptions> options) : SharpbillControllerBase
{
    private readonly SharpbillOptions _options = options.Value;

    [HttpGet("config")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthConfigResponse>> ConfigurationAsync(CancellationToken cancellationToken) =>
        Ok(await authService.GetConfigurationAsync(cancellationToken).ConfigureAwait(false));

    [HttpPost("nonce")]
    [AllowAnonymous]
    public async Task<ActionResult<NonceResponse>> NonceAsync(CancellationToken cancellationToken)
    {
        NonceResponse response = await nonceService.IssueAsync(cancellationToken).ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    [HttpPost("google")]
    [AllowAnonymous]
    public Task<ActionResult<UserResponse>> Google(
        TokenLoginRequest request,
        CancellationToken cancellationToken) =>
        LoginAsync(ProviderContract.Google, request, cancellationToken);

    [HttpPost("microsoft")]
    [AllowAnonymous]
    public Task<ActionResult<UserResponse>> Microsoft(
        TokenLoginRequest request,
        CancellationToken cancellationToken) =>
        LoginAsync(ProviderContract.Microsoft, request, cancellationToken);

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(
            requestContextAccessor.Current,
            cancellationToken).ConfigureAwait(false);
        Response.Cookies.Delete(CookieName, CookieOptions);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserResponse>> MeAsync(CancellationToken cancellationToken) =>
        Ok(await authService.GetCurrentUserAsync(ActorUserId, cancellationToken).ConfigureAwait(false));

    [HttpGet("sessions")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<SessionResponse>>> SessionsAsync(CancellationToken cancellationToken) =>
        Ok(await sessionService.ListAsync(
            ActorUserId,
            ActorUserId,
            includeDeviceDetails: true,
            SessionJti,
            cancellationToken).ConfigureAwait(false));

    [HttpDelete("sessions/{sessionId:int}")]
    [Authorize]
    public async Task<IActionResult> RevokeSessionAsync(int sessionId, CancellationToken cancellationToken)
    {
        await sessionService.RevokeAsync(
            ActorUserId,
            ActorUserId,
            sessionId,
            cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("location")]
    [Authorize]
    public async Task<IActionResult> LocationAsync(
        LocationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        await userService.UpdateLocationAsync(ActorUserId, request, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private async Task<ActionResult<UserResponse>> LoginAsync(
        ProviderContract provider,
        TokenLoginRequest request,
        CancellationToken cancellationToken)
    {
        AuthenticatedSession authenticated = await authService.LoginAsync(
            provider,
            request,
            RequestContext,
            cancellationToken).ConfigureAwait(false);
        SetSessionCookie(authenticated.Session);
        return Ok(authenticated.User);
    }

    private void SetSessionCookie(SessionToken token)
    {
        var cookieOptions = CookieOptions;
        cookieOptions.MaxAge = token.ExpiresAt - token.IssuedAt;
        cookieOptions.Expires = new DateTimeOffset(DateTime.SpecifyKind(token.ExpiresAt, DateTimeKind.Utc));
        Response.Cookies.Append(CookieName, token.Value, cookieOptions);
    }

    private string CookieName => _options.IsLocal
        ? _options.Session.LocalCookieName
        : _options.Session.ProductionCookieName;

    private CookieOptions CookieOptions => new()
    {
        HttpOnly = true,
        Secure = _options.Session.SecureCookie,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        IsEssential = true,
    };
}
