using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;

namespace Sharpbill.Infrastructure.Services.Identity;

internal static class OidcTokenClaims
{
    public static string? GetString(JwtPayload payload, string name)
    {
        if (!payload.TryGetValue(name, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    public static bool IsTrue(JwtPayload payload, string name)
    {
        if (!payload.TryGetValue(name, out object? value) || value is null)
        {
            return false;
        }

        return value switch
        {
            bool boolean => boolean,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            string text => bool.TryParse(text, out bool parsed) && parsed,
            _ => false,
        };
    }

    public static DateTime ReplayDeadline(JwtPayload payload)
    {
        if (payload.Expiration is not { } seconds)
        {
            throw new IdentityTokenException("Token is missing its expiration");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.AddSeconds(30);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new IdentityTokenException("Token expiration is invalid", exception);
        }
    }

    public static DateTime IssuedAt(JwtPayload payload)
    {
        if (!payload.TryGetValue(JwtRegisteredClaimNames.Iat, out object? raw) ||
            !TryGetInt64(raw, out long seconds))
        {
            throw new IdentityTokenException("Token issued-at claim is invalid");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new IdentityTokenException("Token issued-at claim is invalid", exception);
        }
    }

    public static string ReadUnverifiedNonce(string rawToken)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler { MaximumTokenSizeInBytes = 16_384 };
            JwtSecurityToken token = handler.ReadJwtToken(rawToken);
            return GetString(token.Payload, "nonce") ?? string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new IdentityTokenException("Malformed token", exception);
        }
    }

    private static bool TryGetInt64(object? value, out long result)
    {
        switch (value)
        {
            case long number:
                result = number;
                return true;
            case int number:
                result = number;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element:
                return element.TryGetInt64(out result);
            case string text:
                return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }
}
