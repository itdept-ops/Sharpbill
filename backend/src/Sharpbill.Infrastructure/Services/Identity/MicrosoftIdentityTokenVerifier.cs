using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class MicrosoftIdentityTokenVerifier(
    MicrosoftSigningKeyStore signingKeyStore,
    ProviderVerificationRuntime runtime,
    INonceService nonceService,
    IOptions<SharpbillOptions> options) : IIdentityTokenVerifier
{
    private readonly IdentityProviderOptions _options = options.Value.IdentityProviders;

    public ProviderContract Provider => ProviderContract.Microsoft;

    public async Task<VerifiedIdentity> VerifyAsync(
        string idToken,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.MicrosoftClientId))
        {
            throw new IdentityTokenException("Microsoft sign-in is not configured");
        }

        JwtSecurityToken verifiedToken;
        using (await runtime.AcquireVerificationAsync(cancellationToken).ConfigureAwait(false))
        {
            JwtSecurityToken unverified = ReadToken(idToken);
            string keyId = RequireRs256Header(unverified);
            SecurityKey signingKey = await signingKeyStore.GetAsync(
                keyId,
                cancellationToken).ConfigureAwait(false);
            verifiedToken = Validate(idToken, signingKey, _options.MicrosoftClientId);
        }

        JwtPayload claims = verifiedToken.Payload;
        if (OidcTokenClaims.IssuedAt(claims) > runtime.UtcNow.AddSeconds(30))
        {
            throw new IdentityTokenException("Token issued-at claim is in the future");
        }

        string tenantId = RequireCanonicalUuid(OidcTokenClaims.GetString(claims, "tid"), "tid");
        string issuer = OidcTokenClaims.GetString(claims, JwtRegisteredClaimNames.Iss) ?? string.Empty;
        if (!string.Equals(
            issuer,
            $"https://login.microsoftonline.com/{tenantId}/v2.0",
            StringComparison.Ordinal))
        {
            throw new IdentityTokenException("Issuer does not match tenant");
        }

        string objectId = RequireCanonicalUuid(OidcTokenClaims.GetString(claims, "oid"), "oid");
        string email = (OidcTokenClaims.GetString(claims, JwtRegisteredClaimNames.Email) ??
            OidcTokenClaims.GetString(claims, "preferred_username") ?? string.Empty).ToLowerInvariant();
        if (!email.Contains('@', StringComparison.Ordinal))
        {
            throw new IdentityTokenException("Token has no usable email claim");
        }

        string actualNonce = OidcTokenClaims.GetString(claims, "nonce") ?? string.Empty;
        if (string.IsNullOrEmpty(expectedNonce) ||
            !string.Equals(actualNonce, expectedNonce, StringComparison.Ordinal) ||
            !await nonceService.ConsumeAsync(expectedNonce, cancellationToken).ConfigureAwait(false))
        {
            throw new IdentityTokenException("Missing or invalid nonce");
        }

        if (runtime.IsReplay(idToken, OidcTokenClaims.ReplayDeadline(claims)))
        {
            throw new IdentityTokenException("Token was already used");
        }

        return new VerifiedIdentity
        {
            Provider = ProviderContract.Microsoft,
            Subject = objectId,
            Namespace = tenantId,
            Email = email,
            DisplayName = OidcTokenClaims.GetString(claims, "name") ?? email,
            TenantId = tenantId,
        };
    }

    private static JwtSecurityToken Validate(string rawToken, SecurityKey signingKey, string audience)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = false,
            ValidateAudience = true,
            ValidAudience = audience,
            RequireAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        try
        {
            var handler = new JwtSecurityTokenHandler
            {
                MapInboundClaims = false,
                MaximumTokenSizeInBytes = 16_384,
            };
            _ = handler.ValidateToken(rawToken, parameters, out SecurityToken token);
            if (token is not JwtSecurityToken jwt ||
                !jwt.Payload.ContainsKey(JwtRegisteredClaimNames.Iat) ||
                string.IsNullOrEmpty(OidcTokenClaims.GetString(jwt.Payload, JwtRegisteredClaimNames.Sub)))
            {
                throw new IdentityTokenException("Token is missing required claims");
            }

            return jwt;
        }
        catch (IdentityTokenException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException or FormatException)
        {
            throw new IdentityTokenException("Invalid Microsoft token", exception);
        }
    }

    private static JwtSecurityToken ReadToken(string rawToken)
    {
        try
        {
            return new JwtSecurityTokenHandler { MaximumTokenSizeInBytes = 16_384 }.ReadJwtToken(rawToken);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new IdentityTokenException("Malformed token header", exception);
        }
    }

    private static string RequireRs256Header(JwtSecurityToken token)
    {
        if (!string.Equals(token.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal) ||
            token.Header.Kid is not { Length: > 0 } keyId)
        {
            throw new IdentityTokenException("Unsupported token signing header");
        }

        return keyId;
    }

    private static string RequireCanonicalUuid(string? value, string claimName)
    {
        if (value is null || !Guid.TryParseExact(value, "D", out Guid parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new IdentityTokenException($"Missing or malformed {claimName}");
        }

        return value;
    }
}
