using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sharpbill.Application.Common;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class MicrosoftSigningKeyStore(
    ProviderDocumentClient documentClient,
    IClock clock,
    IOptions<SharpbillOptions> options,
    ILogger<MicrosoftSigningKeyStore> logger)
    : ProviderSigningKeyStore("Microsoft", documentClient, clock, options, logger)
{
    private static readonly Uri JwksEndpoint =
        new("https://login.microsoftonline.com/common/discovery/v2.0/keys", UriKind.Absolute);

    protected override Uri Endpoint => JwksEndpoint;

    protected override IReadOnlyDictionary<string, SecurityKey> ParseKeys(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("keys", out JsonElement keysElement) ||
            keysElement.ValueKind != JsonValueKind.Array)
        {
            throw new IdentityProviderUnavailableException("Microsoft JWKS document is malformed");
        }

        foreach (JsonElement key in keysElement.EnumerateArray())
        {
            if (key.ValueKind != JsonValueKind.Object ||
                !key.TryGetProperty("kid", out JsonElement keyId) ||
                keyId.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(keyId.GetString()))
            {
                throw new IdentityProviderUnavailableException("Microsoft JWKS key is malformed");
            }
        }

        JsonWebKeySet keySet;
        try
        {
            keySet = new JsonWebKeySet(document.RootElement.GetRawText());
        }
        catch (ArgumentException exception)
        {
            throw new IdentityProviderUnavailableException(
                "Microsoft returned an unusable JWKS document",
                exception);
        }

        var result = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        foreach (SecurityKey key in keySet.GetSigningKeys())
        {
            if (string.IsNullOrEmpty(key.KeyId) || !result.TryAdd(key.KeyId, key))
            {
                throw new IdentityProviderUnavailableException("Microsoft JWKS key is malformed");
            }
        }

        return result;
    }
}
