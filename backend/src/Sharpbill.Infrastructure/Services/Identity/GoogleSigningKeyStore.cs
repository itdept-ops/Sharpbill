using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sharpbill.Application.Common;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class GoogleSigningKeyStore(
    ProviderDocumentClient documentClient,
    IClock clock,
    IOptions<SharpbillOptions> options,
    ILogger<GoogleSigningKeyStore> logger)
    : ProviderSigningKeyStore("Google", documentClient, clock, options, logger)
{
    private static readonly Uri CertificateEndpoint =
        new("https://www.googleapis.com/oauth2/v1/certs", UriKind.Absolute);

    protected override Uri Endpoint => CertificateEndpoint;

    protected override IReadOnlyDictionary<string, SecurityKey> ParseKeys(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            document.RootElement.TryGetProperty("keys", out _))
        {
            throw new IdentityProviderUnavailableException(
                "Google certificate endpoint returned an unexpected document");
        }

        var keys = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(property.Name) ||
                property.Value.GetString() is not { } certificatePem)
            {
                throw new IdentityProviderUnavailableException(
                    "Google certificate document is malformed");
            }

            X509Certificate2 certificate;
            try
            {
                certificate = X509Certificate2.CreateFromPem(certificatePem);
            }
            catch (CryptographicException exception)
            {
                throw new IdentityProviderUnavailableException(
                    "Google returned an unusable signing certificate",
                    exception);
            }

            keys.Add(property.Name, new X509SecurityKey(certificate) { KeyId = property.Name });
        }

        return keys;
    }
}
