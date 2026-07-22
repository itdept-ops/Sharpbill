using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.IntegrationTests.Configuration;

public sealed class SharpbillOptionsSetupTests
{
    [Fact]
    public void ProductionFullConnectionStringMapsToTheVerifiedRuntimeContract()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APP_ENV"] = "production",
                ["ConnectionStrings:Sharpbill"] =
                    "Server=db.internal;Port=3307;Database=sharpbill;User ID=app;" +
                    "Password=p@ss;SslMode=VerifyFull;SslCa=/certs/database.pem",
                ["SESSION_JWT_SECRET"] = "strong-production-session-secret-0123456789ABCDEF",
                ["PUBLIC_ORIGIN"] = "https://sharpbill.example",
                ["GOOGLE_CLIENT_ID"] = "123456789-testclientid.apps.googleusercontent.com",
                ["EXPORT_MAX_BYTES"] = "8388608",
                ["EXPORT_MAX_CONCURRENCY"] = "3",
            })
            .Build();
        var options = new SharpbillOptions();

        new SharpbillOptionsSetup(configuration).Configure(options);
        ValidateOptionsResult result = new SharpbillOptionsValidator().Validate(null, options);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Failures ?? []));
        Assert.Equal("db.internal", options.Database.Host);
        Assert.Equal((uint)3307, options.Database.Port);
        Assert.Equal("sharpbill", options.Database.Name);
        Assert.Equal("app", options.Database.User);
        Assert.Equal("p@ss", options.Database.Password);
        Assert.True(options.Database.RequireTls);
        Assert.Equal("/certs/database.pem", options.Database.TlsCaPath);
        Assert.Equal(8_388_608, options.RequestPipeline.ExportMaxBytes);
        Assert.Equal(3, options.RequestPipeline.ExportMaxConcurrency);
    }

    [Fact]
    public void CompleteAndDiscreteDatabaseConfigurationCannotBeMixed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Sharpbill"] =
                    "Server=db;Database=sharpbill;User ID=app;Password=test;SslMode=Disabled",
                ["DB_HOST"] = "other-db",
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new SharpbillOptionsSetup(configuration).Configure(new SharpbillOptions()));

        Assert.Contains("mutually exclusive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedProxyConfigurationAcceptsAddressesAndCidrNetworks()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TRUSTED_PROXY_IPS"] = "127.0.0.1, 10.0.0.0/8, 2001:db8::/32",
            })
            .Build();
        var options = new SharpbillOptions();

        new SharpbillOptionsSetup(configuration).Configure(options);

        Assert.Equal("127.0.0.1", Assert.Single(options.TrustedProxies).ToString());
        Assert.Equal(
            ["10.0.0.0/8", "2001:db8::/32"],
            options.TrustedProxyNetworks.Select(static network => network.ToString()).ToArray());
    }

    [Fact]
    public void TrustedProxyConfigurationRejectsInvalidEntries()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TRUSTED_PROXY_IPS"] = "127.0.0.1,not-a-network",
            })
            .Build();

        FormatException exception = Assert.Throws<FormatException>(
            () => new SharpbillOptionsSetup(configuration).Configure(new SharpbillOptions()));

        Assert.Contains("TRUSTED_PROXY_IPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionConfigurationRejectsWorldwideProxyNetworks()
    {
        var options = new SharpbillOptions
        {
            AppEnvironment = "production",
            TrustedProxyNetworks = [System.Net.IPNetwork.Parse("0.0.0.0/0")],
        };

        ValidateOptionsResult result = new SharpbillOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            static failure => failure.Contains("world-wide CIDR", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("replace-me-development-secret-000000000000")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("short")]
    public void DevelopmentAuthenticationRejectsWeakSecrets(string secret)
    {
        Assert.False(DevelopmentAuthenticationGuard.IsStrongIndependentSecret(
            secret,
            "independent-session-secret-0123456789abcdef"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public void ExportConcurrencyRejectsUnsafeBounds(int concurrency)
    {
        var options = new SharpbillOptions();
        options.RequestPipeline.ExportMaxConcurrency = concurrency;

        ValidateOptionsResult result = new SharpbillOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            static failure => failure.Contains("EXPORT_MAX_CONCURRENCY", StringComparison.Ordinal));
    }
}
