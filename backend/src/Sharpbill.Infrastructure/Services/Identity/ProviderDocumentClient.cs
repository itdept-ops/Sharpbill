using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public static class IdentityProviderHttpClientNames
{
    public const string SigningKeys = "Sharpbill.IdentityProvider.SigningKeys";
}

public sealed class ProviderDocumentClient(
    IHttpClientFactory httpClientFactory,
    ProviderVerificationRuntime runtime,
    IOptions<SharpbillOptions> options)
{
    private readonly IdentityProviderOptions _options = options.Value.IdentityProviders;

    public async Task<JsonDocument> FetchAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using IDisposable lease = await runtime.AcquireNetworkAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            _options.ConnectTimeoutSeconds + _options.ReadTimeoutSeconds));

        try
        {
            HttpClient client = httpClientFactory.CreateClient(IdentityProviderHttpClientNames.SigningKeys);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("sharpbill/1");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new IdentityProviderUnavailableException(
                    $"Provider key endpoint returned HTTP {(int)response.StatusCode}");
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(Math.Min(_options.KeyDocumentMaxBytes, 65_536));
            byte[] chunk = new byte[65_536];
            int total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > _options.KeyDocumentMaxBytes)
                {
                    throw new IdentityProviderUnavailableException(
                        "Provider key document exceeded the configured size limit");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }

            buffer.Position = 0;
            return await JsonDocument.ParseAsync(
                buffer,
                new JsonDocumentOptions { MaxDepth = 32 },
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IdentityProviderUnavailableException(
                "Provider signing-key request timed out",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new IdentityProviderUnavailableException(
                "Provider signing-key request failed",
                exception);
        }
        catch (JsonException exception)
        {
            throw new IdentityProviderUnavailableException(
                "Provider returned an invalid signing-key document",
                exception);
        }
    }
}
