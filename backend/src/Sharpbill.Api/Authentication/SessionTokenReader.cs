using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sharpbill.Application.Common;
using Sharpbill.Infrastructure.Configuration;
using SharpbillSessionOptions = Sharpbill.Infrastructure.Configuration.SessionOptions;

namespace Sharpbill.Api.Authentication;

public sealed record ValidatedSessionToken(
    int UserId,
    Guid Jti,
    DateTime IssuedAt,
    long IssuedAtSeconds);

public sealed record SessionTokenReadResult(
    ValidatedSessionToken? Token,
    Exception? ValidationException)
{
    public bool IsValid => Token is not null;
}

/// <summary>
/// Performs the cryptographic and structural validation that is shared by HTTP and WebSocket
/// session authentication. Database-backed session state is deliberately validated separately.
/// </summary>
public sealed class SessionTokenReader(
    IOptions<SharpbillOptions> options,
    IClock clock)
{
    private readonly SharpbillSessionOptions _options = options.Value.Session;

    public SessionTokenReadResult Read(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        try
        {
            ClaimsPrincipal tokenPrincipal = ValidateToken(rawToken, out SecurityToken validatedToken);
            if (validatedToken is not JwtSecurityToken jwt ||
                !string.Equals(jwt.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal) ||
                !string.Equals(jwt.Header.Typ, "JWT", StringComparison.Ordinal) ||
                !string.Equals(tokenPrincipal.FindFirstValue("token_type"), "session", StringComparison.Ordinal) ||
                !int.TryParse(tokenPrincipal.FindFirstValue(JwtRegisteredClaimNames.Sub), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int userId) ||
                !Guid.TryParse(tokenPrincipal.FindFirstValue(JwtRegisteredClaimNames.Jti), out Guid jti) ||
                !long.TryParse(tokenPrincipal.FindFirstValue(JwtRegisteredClaimNames.Iat), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long issuedAtSeconds) ||
                issuedAtSeconds > new DateTimeOffset(
                    DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc)).ToUnixTimeSeconds())
            {
                return new SessionTokenReadResult(null, null);
            }

            DateTime issuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds).UtcDateTime;
            return new SessionTokenReadResult(
                new ValidatedSessionToken(userId, jti, issuedAt, issuedAtSeconds),
                null);
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException or FormatException)
        {
            return new SessionTokenReadResult(null, exception);
        }
    }

    private ClaimsPrincipal ValidateToken(string rawToken, out SecurityToken validatedToken)
    {
        Dictionary<string, SecurityKey> keyring = BuildKeyring(_options);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
            IssuerSigningKeyResolver = (_, _, kid, _) =>
                kid is not null && keyring.TryGetValue(kid, out SecurityKey? key) ? [key] : [],
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        return handler.ValidateToken(rawToken, parameters, out validatedToken);
    }

    private static Dictionary<string, SecurityKey> BuildKeyring(SharpbillSessionOptions options) =>
        new[] { options.ActiveSecret }.Concat(options.PreviousSecrets)
            .Where(static secret => !string.IsNullOrWhiteSpace(secret))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                static secret => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..16],
                static secret => (SecurityKey)new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                StringComparer.Ordinal);
}
