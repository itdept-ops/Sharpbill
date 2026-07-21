using System.Globalization;
using System.Security.Claims;
using Sharpbill.Api.Controllers;
using Sharpbill.Contracts.Users;

namespace Sharpbill.Api.Authentication;

public static class SessionPrincipalFactory
{
    public static ClaimsPrincipal Create(
        UserResponse user,
        Guid sessionJti,
        DateTime issuedAt)
    {
        ArgumentNullException.ThrowIfNull(user);
        long issuedAtSeconds = new DateTimeOffset(DateTime.SpecifyKind(issuedAt, DateTimeKind.Utc))
            .ToUnixTimeSeconds();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.DisplayName ?? user.Email),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role),
            new(SharpbillClaimTypes.SessionJti, sessionJti.ToString()),
            new(SharpbillClaimTypes.SessionIssuedAt, issuedAtSeconds.ToString(CultureInfo.InvariantCulture)),
        };
        claims.AddRange(user.Permissions.Select(static permission =>
            new Claim(SharpbillClaimTypes.Permission, permission)));
        var identity = new ClaimsIdentity(
            claims,
            SessionAuthenticationDefaults.Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
