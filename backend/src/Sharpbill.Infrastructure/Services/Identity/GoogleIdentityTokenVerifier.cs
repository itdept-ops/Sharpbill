using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class GoogleIdentityTokenVerifier(
    GoogleSigningKeyStore signingKeyStore,
    ProviderVerificationRuntime runtime,
    INonceService nonceService,
    IOptions<SharpbillOptions> options) : IIdentityTokenVerifier
{
    private static readonly string[] ValidIssuers =
        ["accounts.google.com", "https://accounts.google.com"];
    private readonly IdentityProviderOptions _options = options.Value.IdentityProviders;

    public ProviderContract Provider => ProviderContract.Google;

    public async Task<VerifiedIdentity> VerifyAsync(
        string idToken,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.GoogleClientId))
        {
            throw new IdentityTokenException("Google sign-in is not configured");
        }

        JwtSecurityToken verifiedToken;
        using (await runtime.AcquireVerificationAsync(cancellationToken).ConfigureAwait(false))
        {
            JwtSecurityToken unverified = ReadToken(idToken);
            string keyId = RequireRs256Header(unverified);
            SecurityKey signingKey = await signingKeyStore.GetAsync(
                keyId,
                cancellationToken).ConfigureAwait(false);
            verifiedToken = Validate(idToken, signingKey, _options.GoogleClientId);
        }

        JwtPayload claims = verifiedToken.Payload;
        if (!HasValidAuthorizedParty(verifiedToken, _options.GoogleClientId))
        {
            throw new IdentityTokenException("Google token authorized party does not match this client");
        }

        if (OidcTokenClaims.IssuedAt(claims) > runtime.UtcNow.AddSeconds(30))
        {
            throw new IdentityTokenException("Token issued-at claim is in the future");
        }

        string issuer = OidcTokenClaims.GetString(claims, JwtRegisteredClaimNames.Iss) ?? string.Empty;
        if (!ValidIssuers.Contains(issuer, StringComparer.Ordinal))
        {
            throw new IdentityTokenException("Wrong issuer");
        }

        if (!OidcTokenClaims.IsTrue(claims, "email_verified"))
        {
            throw new IdentityTokenException("Google email is not verified");
        }

        string subject = OidcTokenClaims.GetString(claims, JwtRegisteredClaimNames.Sub) ?? string.Empty;
        string email = OidcTokenClaims.GetString(claims, JwtRegisteredClaimNames.Email) ?? string.Empty;
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email))
        {
            throw new IdentityTokenException("Token is missing sub/email");
        }

        await RequireSingleUseNonceAsync(claims, expectedNonce, cancellationToken).ConfigureAwait(false);
        if (runtime.IsReplay(idToken, OidcTokenClaims.ReplayDeadline(claims)))
        {
            throw new IdentityTokenException("Token was already used");
        }

        string? hostedDomain = OidcTokenClaims.GetString(claims, "hd");
        return new VerifiedIdentity
        {
            Provider = ProviderContract.Google,
            Subject = subject,
            Email = email.ToLowerInvariant(),
            DisplayName = OidcTokenClaims.GetString(claims, "name") ?? email,
            HostedDomain = hostedDomain?.ToLowerInvariant(),
        };
    }

    private async Task RequireSingleUseNonceAsync(
        JwtPayload claims,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        string actualNonce = OidcTokenClaims.GetString(claims, "nonce") ?? string.Empty;
        if (string.IsNullOrEmpty(expectedNonce) ||
            !string.Equals(actualNonce, expectedNonce, StringComparison.Ordinal) ||
            !await nonceService.ConsumeAsync(expectedNonce, cancellationToken).ConfigureAwait(false))
        {
            throw new IdentityTokenException("Missing or invalid nonce");
        }
    }

    private static JwtSecurityToken Validate(string rawToken, SecurityKey signingKey, string audience)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidIssuers = ValidIssuers,
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
            if (token is not JwtSecurityToken jwt || !jwt.Payload.ContainsKey(JwtRegisteredClaimNames.Iat))
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
            throw new IdentityTokenException("Invalid Google token", exception);
        }
    }

    internal static bool HasValidAuthorizedParty(JwtSecurityToken token, string clientId)
    {
        string[] audiences = token.Audiences.Distinct(StringComparer.Ordinal).ToArray();
        string? authorizedParty = OidcTokenClaims.GetString(token.Payload, "azp");
        if (authorizedParty is not null &&
            !string.Equals(authorizedParty, clientId, StringComparison.Ordinal))
        {
            return false;
        }

        return audiences.Length <= 1 ||
            string.Equals(authorizedParty, clientId, StringComparison.Ordinal);
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
}
