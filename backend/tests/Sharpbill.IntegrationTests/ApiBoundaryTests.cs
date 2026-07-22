using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Api.Diagnostics;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Health;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Services.Identity;
using Sharpbill.Infrastructure.Services.Operations;

namespace Sharpbill.IntegrationTests;

public sealed class ApiBoundaryTests(SharpbillApiFactory factory) : IClassFixture<SharpbillApiFactory>
{
    private readonly SharpbillApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    [Fact]
    public async Task LivenessIsDependencyFreeAndSetsBoundaryHeadersAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/api/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store, max-age=0", response.Headers.CacheControl?.ToString());
        Assert.True(response.Headers.Contains("X-Request-ID"));
        LivenessResponse? payload = await response.Content.ReadFromJsonAsync<LivenessResponse>();
        Assert.Equal("alive", payload?.Status);
    }

    [Fact]
    public async Task DuplicateClientCorrelationIdsReceiveUniqueServerRequestIdsAsync()
    {
        const string clientRequestId = "client-correlation-42";
        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/api/health/live");
        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/api/health/live");
        firstRequest.Headers.Add("X-Request-ID", clientRequestId);
        secondRequest.Headers.Add("X-Request-ID", clientRequestId);

        using HttpResponseMessage firstResponse = await _client.SendAsync(firstRequest);
        using HttpResponseMessage secondResponse = await _client.SendAsync(secondRequest);
        string firstServerId = Assert.Single(firstResponse.Headers.GetValues("X-Request-ID"));
        string secondServerId = Assert.Single(secondResponse.Headers.GetValues("X-Request-ID"));

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Matches("^[a-f0-9]{32}$", firstServerId);
        Assert.Matches("^[a-f0-9]{32}$", secondServerId);
        Assert.NotEqual(firstServerId, secondServerId);
        Assert.NotEqual(clientRequestId, firstServerId);
        Assert.Equal(
            clientRequestId,
            Assert.Single(firstResponse.Headers.GetValues("X-Client-Request-ID")));
        Assert.Equal(
            clientRequestId,
            Assert.Single(secondResponse.Headers.GetValues("X-Client-Request-ID")));
    }

    [Fact]
    public async Task InvalidClientCorrelationIdIsNotPropagatedAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health/live");
        request.Headers.TryAddWithoutValidation("X-Request-ID", "contains spaces");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Request-ID"));
        Assert.False(response.Headers.Contains("X-Client-Request-ID"));
    }

    [Fact]
    public async Task LivenessIgnoresAnOtherwiseValidSessionCookieAsync()
    {
        IOptions<SharpbillOptions> options =
            _factory.Services.GetRequiredService<IOptions<SharpbillOptions>>();
        var issuer = new SessionJwtIssuer(options);
        SessionToken token = issuer.Issue(4242, Guid.NewGuid(), DateTime.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health/live");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            $"{options.Value.Session.LocalCookieName}={token.Value}");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpointUsesStableErrorEnvelopeAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/api/auth/me", UriKind.Relative));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("\"code\":\"NOT_AUTHENTICATED\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("traceId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryApiEndpointDeclaresAuthorizationOrAnonymousAccess()
    {
        EndpointDataSource source = _factory.Services.GetRequiredService<EndpointDataSource>();
        RouteEndpoint[] apiEndpoints = source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(static endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith("api/", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.NotEmpty(apiEndpoints);
        Assert.All(apiEndpoints, static endpoint =>
        {
            bool hasAuthorization = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
            bool allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            Assert.True(
                hasAuthorization || allowsAnonymous,
                $"Endpoint '{endpoint.RoutePattern.RawText}' must explicitly declare authorization or anonymous access.");
        });
    }

    [Fact]
    public void RepeatedBoundaryRejectionsAreSampledPerClientAndCategory()
    {
        BoundaryRejectionLogger logger =
            _factory.Services.GetRequiredService<BoundaryRejectionLogger>();
        long before = logger.SuppressedTotal;
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "boundary-sampling-test";
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/sampling-test";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.254");

        logger.Record(context, "test_boundary", "TEST_REJECTED", 403);
        logger.Record(context, "test_boundary", "TEST_REJECTED", 403);

        Assert.Equal(before + 1, logger.SuppressedTotal);
    }

    [Fact]
    public async Task CrossOriginMutationIsRejectedBeforeAuthenticationAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/presence/heartbeat", UriKind.Relative));
        request.Headers.Add("Origin", "https://attacker.example");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Content = JsonContent.Create(new { });

        using HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"code\":\"CSRF_REJECTED\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidOriginTakesPrecedenceOverRefererAsync()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/presence/heartbeat", UriKind.Relative));
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add("Referer", "https://attacker.example/form");
        request.Content = JsonContent.Create(new { });

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SameOriginFetchMetadataIsTrustedWithoutOriginHeadersAsync()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/presence/heartbeat", UriKind.Relative));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Content = JsonContent.Create(new { });

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MixedCaseRoutedMutationIsDurablyLoggedAsync()
    {
        SharpbillApiFactory.CapturingRequestLogBuffer buffer =
            _factory.Services.GetRequiredService<SharpbillApiFactory.CapturingRequestLogBuffer>();
        buffer.Clear();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/API/users/bulk");
        request.Headers.Add("Origin", "http://localhost");
        int[] userIds = [1];
        request.Content = JsonContent.Create(new { action = "disable", user_ids = userIds });

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await WaitUntilAsync(
            () => buffer.Items.Any(static item =>
                string.Equals(item.Path, "/API/users/bulk", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(2));
        Assert.Contains(buffer.Items, static item =>
            string.Equals(item.Path, "/API/users/bulk", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongApiPathIsClampedBeforeDurableLoggingAsync()
    {
        SharpbillApiFactory.CapturingRequestLogBuffer buffer =
            _factory.Services.GetRequiredService<SharpbillApiFactory.CapturingRequestLogBuffer>();
        buffer.Clear();
        string path = "/api/" + new string('x', 400);

        using HttpResponseMessage response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await WaitUntilAsync(
            () => buffer.Items.Any(static log => log.Path.Length == 255),
            TimeSpan.FromSeconds(2));
        RequestLog item = Assert.Single(buffer.Items, static log => log.Path.Length == 255);
        Assert.Equal("GET", item.Method);
        Assert.True(item.IpAddress is null || item.IpAddress.Length <= 45);
    }

    [Fact]
    public async Task ChunkedDeleteBodyIsRejectedBeforeRoutingAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/privacy/location")
        {
            Content = new UnknownLengthContent(1_048_577),
        };

        using HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("\"code\":\"REQUEST_TOO_LARGE\"", body, StringComparison.Ordinal);
        Assert.Contains("Request body exceeds the allowed size", body, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }

    private sealed class UnknownLengthContent(int byteCount) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => WriteAsync(stream, CancellationToken.None);

        protected override void SerializeToStream(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            WriteAsync(stream, cancellationToken).GetAwaiter().GetResult();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[16 * 1024];
            int remaining = byteCount;
            while (remaining > 0)
            {
                int count = Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                remaining -= count;
            }
        }
    }
}

public sealed class SharpbillApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["APP_ENV"] = "local",
                ["DB_HOST"] = "localhost",
                ["DB_NAME"] = "sharpbill_test",
                ["DB_USER"] = "sharpbill_test",
                ["DB_PASSWORD"] = "integration-only-password",
                ["SESSION_JWT_SECRET"] = "integration-session-secret-000000000000000000000000",
                ["COOKIE_SECURE"] = "false",
                ["PUBLIC_ORIGIN"] = "http://localhost",
                ["RETENTION_WORKER_INTERVAL_SECONDS"] = "3600",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHealthService>();
            services.AddScoped<IHealthService, StubHealthService>();
            services.RemoveAll<IRequestLogBuffer>();
            services.AddSingleton<CapturingRequestLogBuffer>();
            services.AddSingleton<IRequestLogBuffer>(static provider =>
                provider.GetRequiredService<CapturingRequestLogBuffer>());
        });
    }

    public sealed class CapturingRequestLogBuffer : IRequestLogBuffer
    {
        private readonly ConcurrentQueue<RequestLog> _items = new();

        public IReadOnlyCollection<RequestLog> Items => _items.ToArray();

        public bool TryWrite(RequestLog requestLog)
        {
            _items.Enqueue(requestLog);
            return true;
        }

        public Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public RequestLogMetricsResponse GetMetrics() => new();

        public void Clear()
        {
            while (_items.TryDequeue(out _))
            {
            }
        }
    }

    private sealed class StubHealthService : IHealthService
    {
        public LivenessResponse GetLiveness() => new();

        public Task<(ReadinessResponse Response, bool IsReady)> GetReadinessAsync(
            CancellationToken cancellationToken) => Task.FromResult((new ReadinessResponse
            {
                Status = "ready",
                Database = "ok",
                Schema = "ok",
                IdentityProvider = "ok",
                Administration = "ok",
                AdmissionPolicy = "ok",
            }, true));
    }
}
