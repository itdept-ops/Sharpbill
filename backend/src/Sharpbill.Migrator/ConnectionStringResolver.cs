using System.Globalization;
using MySqlConnector;

namespace Sharpbill.Migrator;

internal sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message)
        : base(message)
    {
    }
}

internal static class ConnectionStringResolver
{
    public static string Resolve(CliOptions options) =>
        Resolve(options, Environment.GetEnvironmentVariable);

    internal static string Resolve(CliOptions options, Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readEnvironment);

        bool production = !string.Equals(
            ReadFirst(readEnvironment, "APP_ENV"),
            "local",
            StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return Validate(options.ConnectionString, production);
        }

        string? configured = ReadFirst(
            readEnvironment,
            "ConnectionStrings__Sharpbill",
            "SHARPBILL_DATABASE_CONNECTION");

        string? host = ReadFirst(readEnvironment, "DB_HOST", "SHARPBILL_DB_HOST");
        string? port = ReadFirst(readEnvironment, "DB_PORT", "SHARPBILL_DB_PORT");
        string? database = ReadFirst(readEnvironment, "DB_NAME", "SHARPBILL_DB_NAME");
        string? user = ReadFirst(readEnvironment, "DB_USER", "SHARPBILL_DB_USER");
        string? password = ReadFirstAllowEmpty(readEnvironment, "DB_PASSWORD", "SHARPBILL_DB_PASSWORD");
        string? requireTls = ReadFirst(readEnvironment, "DB_REQUIRE_TLS", "SHARPBILL_DB_REQUIRE_TLS");
        string? tlsCaPath = ReadFirst(readEnvironment, "DB_TLS_CA_PATH", "SHARPBILL_DB_TLS_CA_PATH");
        string? legacySslMode = ReadFirst(readEnvironment, "DB_SSL_MODE", "SHARPBILL_DB_SSL_MODE");

        bool hasDiscreteConfiguration =
            host is not null
            || port is not null
            || database is not null
            || user is not null
            || password is not null
            || requireTls is not null
            || tlsCaPath is not null
            || legacySslMode is not null;

        if (configured is not null && hasDiscreteConfiguration)
        {
            throw new ConfigurationException(
                "ConnectionStrings__Sharpbill and discrete DB_* connection settings are mutually exclusive.");
        }

        if (configured is not null)
        {
            return Validate(configured, production);
        }

        if (hasDiscreteConfiguration)
        {
            if (string.IsNullOrWhiteSpace(host)
                || string.IsNullOrWhiteSpace(database)
                || string.IsNullOrWhiteSpace(user)
                || password is null)
            {
                throw new ConfigurationException(
                    "Discrete database configuration requires DB_HOST, DB_NAME, DB_USER, and "
                    + "DB_PASSWORD (or all equivalent SHARPBILL_DB_* aliases). DB_PORT is optional.");
            }

            uint parsedPort = 3306;
            if (port is not null
                && (!uint.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out parsedPort)
                    || parsedPort is 0 or > 65535))
            {
                throw new ConfigurationException("DB_PORT must be an integer from 1 through 65535.");
            }

            bool parsedRequireTls = ParseBoolean(requireTls, "DB_REQUIRE_TLS", fallback: false);
            MySqlSslMode sslMode = parsedRequireTls ? MySqlSslMode.VerifyFull : MySqlSslMode.Disabled;
            if (legacySslMode is not null)
            {
                if (!Enum.TryParse(legacySslMode, ignoreCase: true, out MySqlSslMode parsedLegacyMode))
                {
                    throw new ConfigurationException(
                        "DB_SSL_MODE must be Disabled, Preferred, Required, VerifyCA, or VerifyFull.");
                }

                if (requireTls is not null && parsedLegacyMode != sslMode)
                {
                    throw new ConfigurationException(
                        "DB_SSL_MODE conflicts with DB_REQUIRE_TLS; remove DB_SSL_MODE and use the shared TLS contract.");
                }

                sslMode = parsedLegacyMode;
            }

            EnsureTransportPolicy(production, sslMode, tlsCaPath);
            var builder = new MySqlConnectionStringBuilder
            {
                Server = host,
                Port = parsedPort,
                Database = database,
                UserID = user,
                Password = password,
                CharacterSet = "utf8mb4",
                SslMode = sslMode,
                SslCa = sslMode is MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull
                    ? tlsCaPath
                    : string.Empty,
                AllowPublicKeyRetrieval = !production && sslMode == MySqlSslMode.Disabled,
                AllowLoadLocalInfile = false,
                AllowUserVariables = false,
                ConnectionReset = true,
                ConnectionTimeout = 15,
                DefaultCommandTimeout = 60,
                Pooling = true,
            };
            return builder.ConnectionString;
        }

        throw new ConfigurationException(
            "No database connection was configured. Supply --connection, discrete DB_* "
            + "variables, or ConnectionStrings__Sharpbill.");
    }

    private static string Validate(string connectionString, bool production)
    {
        MySqlConnectionStringBuilder builder;
        try
        {
            builder = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new ConfigurationException(
                $"The database connection string is invalid: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(builder.Server)
            || string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.UserID))
        {
            throw new ConfigurationException(
                "The connection string must select a server, database, and user. The migrator "
                + "does not create databases or choose a default schema.");
        }

        EnsureTransportPolicy(production, builder.SslMode, builder.SslCa);
        builder.AllowPublicKeyRetrieval = !production && builder.SslMode == MySqlSslMode.Disabled;
        builder.AllowLoadLocalInfile = false;
        builder.AllowUserVariables = false;
        return builder.ConnectionString;
    }

    private static void EnsureTransportPolicy(
        bool production,
        MySqlSslMode sslMode,
        string? tlsCaPath)
    {
        if (production && sslMode != MySqlSslMode.VerifyFull)
        {
            throw new ConfigurationException(
                "Production database connections require DB_REQUIRE_TLS=true (SslMode=VerifyFull).");
        }

        if (sslMode is MySqlSslMode.VerifyCA or MySqlSslMode.VerifyFull &&
            string.IsNullOrWhiteSpace(tlsCaPath))
        {
            throw new ConfigurationException(
                "DB_TLS_CA_PATH (SslCa) is required for certificate-verifying database connections.");
        }
    }

    private static bool ParseBoolean(string? raw, string name, bool fallback)
    {
        if (raw is null)
        {
            return fallback;
        }

        if (!bool.TryParse(raw, out bool value))
        {
            throw new ConfigurationException($"{name} must be true or false.");
        }

        return value;
    }

    private static string? ReadFirst(Func<string, string?> readEnvironment, params string[] names)
    {
        foreach (string name in names)
        {
            string? value = readEnvironment(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadFirstAllowEmpty(
        Func<string, string?> readEnvironment,
        params string[] names)
    {
        foreach (string name in names)
        {
            string? value = readEnvironment(name);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }
}
