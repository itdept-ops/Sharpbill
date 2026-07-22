using MySqlConnector;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.IntegrationTests.Database;

public sealed class MySqlConnectionFactoryTests
{
    [Fact]
    public void LocalNonTlsConnectionEnablesPublicKeyRetrieval()
    {
        var options = new SharpbillOptions
        {
            AppEnvironment = "local",
            Database = new DatabaseOptions
            {
                Host = "db",
                Name = "sharpbill",
                User = "app",
                Password = "test-only",
                RequireTls = false,
            },
        };

        var parsed = new MySqlConnectionStringBuilder(
            MySqlConnectionFactory.BuildConnectionString(options));

        Assert.Equal(MySqlSslMode.Disabled, parsed.SslMode);
        Assert.True(parsed.AllowPublicKeyRetrieval);
    }

    [Fact]
    public void ProductionVerifiedTlsConnectionDisablesPublicKeyRetrieval()
    {
        var options = new SharpbillOptions
        {
            AppEnvironment = "production",
            Database = new DatabaseOptions
            {
                Host = "db",
                Name = "sharpbill",
                User = "app",
                Password = "test-only",
                RequireTls = true,
                TlsCaPath = "/certs/database-ca.pem",
            },
        };

        var parsed = new MySqlConnectionStringBuilder(
            MySqlConnectionFactory.BuildConnectionString(options));

        Assert.Equal(MySqlSslMode.VerifyFull, parsed.SslMode);
        Assert.False(parsed.AllowPublicKeyRetrieval);
        Assert.Equal("/certs/database-ca.pem", parsed.SslCa);
    }
}
