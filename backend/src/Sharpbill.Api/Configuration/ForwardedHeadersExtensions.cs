using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Configuration;

public static class ForwardedHeadersExtensions
{
    public static IApplicationBuilder UseSharpbillForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        SharpbillOptions settings = app.ApplicationServices.GetRequiredService<IOptions<SharpbillOptions>>().Value;
        if (settings.TrustedProxies.Count == 0 && settings.TrustedProxyNetworks.Count == 0)
        {
            return app;
        }

        ForwardedHeadersOptions options = new()
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost,
            // With an explicit trust set, walk right-to-left through every trusted proxy and stop
            // at the first untrusted hop. This preserves the original client across multi-hop
            // ingress chains without accepting arbitrary forwarded headers.
            ForwardLimit = null,
            // Trusted ingress commonly sends For/Proto without Host. Requiring all three would
            // silently discard otherwise valid forwarding information.
            RequireHeaderSymmetry = false,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (System.Net.IPAddress proxy in settings.TrustedProxies)
        {
            options.KnownProxies.Add(proxy);
        }

        foreach (System.Net.IPNetwork network in settings.TrustedProxyNetworks)
        {
            options.KnownIPNetworks.Add(network);
        }

        return app.UseForwardedHeaders(options);
    }
}
