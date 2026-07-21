using MySqlConnector;

namespace Sharpbill.Migrator.Tests;

public sealed class ConnectionStringResolverTests
{
    private static readonly CliOptions Options = new(
        MigratorCommand.Validate,
        DryRun: false,
        ConnectionString: null,
        CliOptions.DefaultLockTimeoutSeconds);

    [Fact]
    public void ProductionDiscreteConfigurationRequiresAndAppliesVerifiedTls()
    {
        var environment = BaseEnvironment("production");
        environment["DB_REQUIRE_TLS"] = "true";
        environment["DB_TLS_CA_PATH"] = "/certs/database-ca.pem";

        string value = ConnectionStringResolver.Resolve(Options, environment.GetValueOrDefault);
        var parsed = new MySqlConnectionStringBuilder(value);

        Assert.Equal(MySqlSslMode.VerifyFull, parsed.SslMode);
        Assert.Equal("/certs/database-ca.pem", parsed.SslCa);
        Assert.False(parsed.AllowLoadLocalInfile);
        Assert.False(parsed.AllowUserVariables);
    }

    [Fact]
    public void ProductionDiscreteConfigurationRejectsDowngradeableTls()
    {
        var environment = BaseEnvironment("production");
        environment["DB_REQUIRE_TLS"] = "false";

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConnectionStringResolver.Resolve(Options, environment.GetValueOrDefault));

        Assert.Contains("VerifyFull", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionFullConnectionStringRejectsDowngradeableTls()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP_ENV"] = "production",
            ["ConnectionStrings__Sharpbill"] =
                "Server=db;Database=sharpbill;User ID=migrator;Password=test;SslMode=Preferred",
        };

        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConnectionStringResolver.Resolve(Options, environment.GetValueOrDefault));

        Assert.Contains("VerifyFull", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalDiscreteConfigurationDefaultsToDisabledTls()
    {
        var environment = BaseEnvironment("local");

        string value = ConnectionStringResolver.Resolve(Options, environment.GetValueOrDefault);

        Assert.Equal(MySqlSslMode.Disabled, new MySqlConnectionStringBuilder(value).SslMode);
    }

    private static Dictionary<string, string?> BaseEnvironment(string environment) =>
        new(StringComparer.Ordinal)
        {
            ["APP_ENV"] = environment,
            ["DB_HOST"] = "db",
            ["DB_NAME"] = "sharpbill",
            ["DB_USER"] = "migrator",
            ["DB_PASSWORD"] = "test-only",
        };
}
