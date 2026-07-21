using System.Net;
using System.Net.Http.Headers;

namespace Sharpbill.Api.Diagnostics;

public static class HealthCheckCommand
{
    public static async Task<int> RunAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine("Health-check URL must be an absolute HTTP(S) URL.");
            return 2;
        }

        using HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("sharpbill-healthcheck", "1.0"));
        try
        {
            using HttpResponseMessage response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine("Health check failed.");
            return 1;
        }
    }
}
