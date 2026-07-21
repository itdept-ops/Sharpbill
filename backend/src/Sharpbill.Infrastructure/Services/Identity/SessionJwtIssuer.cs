using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sharpbill.Contracts.Auth;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class SessionJwtIssuer(IOptions<SharpbillOptions> options)
{
    private const string TokenType = "session";
    private readonly SessionOptions _options = options.Value.Session;

    public SessionToken Issue(int userId, Guid jti, DateTime issuedAt)
    {
        DateTime utcIssuedAt = DateTime.SpecifyKind(issuedAt, DateTimeKind.Utc);
        DateTime expiresAt = utcIssuedAt.AddHours(_options.LifetimeHours);
        byte[] secret = Encoding.UTF8.GetBytes(_options.ActiveSecret);
        string keyId = Convert.ToHexStringLower(SHA256.HashData(secret))[..16];
        var key = new SymmetricSecurityKey(secret) { KeyId = keyId };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new Claim[]
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, jti.ToString("N", CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Iat,
                new DateTimeOffset(utcIssuedAt).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new("token_type", TokenType),
        };
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            notBefore: null,
            expires: expiresAt,
            signingCredentials: credentials);
        token.Header["typ"] = "JWT";
        token.Header["kid"] = keyId;

        return new SessionToken
        {
            Value = new JwtSecurityTokenHandler().WriteToken(token),
            Jti = jti,
            UserId = userId,
            IssuedAt = utcIssuedAt,
            ExpiresAt = expiresAt,
        };
    }
}
