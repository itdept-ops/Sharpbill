using System.Globalization;

namespace Sharpbill.Migrator;

internal enum MigratorCommand
{
    Validate,
    Migrate,
    SeedDemo,
}

internal sealed record CliOptions(
    MigratorCommand Command,
    bool DryRun,
    string? ConnectionString,
    int LockTimeoutSeconds)
{
    public const int DefaultLockTimeoutSeconds = 60;

    public static bool TryParse(
        IReadOnlyList<string> args,
        out CliOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (args.Count == 0 || IsHelp(args[0]))
        {
            error = args.Count == 0 ? "A command is required." : null;
            return false;
        }

        MigratorCommand command;
        if (string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
        {
            command = MigratorCommand.Validate;
        }
        else if (string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase))
        {
            command = MigratorCommand.Migrate;
        }
        else if (string.Equals(args[0], "seed-demo", StringComparison.OrdinalIgnoreCase))
        {
            command = MigratorCommand.SeedDemo;
        }
        else
        {
            error = $"Unknown command '{args[0]}'. Expected 'validate', 'migrate', or 'seed-demo'.";
            return false;
        }

        bool dryRun = false;
        string? connectionString = null;
        int lockTimeoutSeconds = DefaultLockTimeoutSeconds;

        for (int index = 1; index < args.Count; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, "--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
                continue;
            }

            if (TryReadValue(args, ref index, "--connection", out string? connection))
            {
                if (string.IsNullOrWhiteSpace(connection))
                {
                    error = "--connection requires a non-empty connection string.";
                    return false;
                }

                connectionString = connection;
                continue;
            }

            if (TryReadValue(args, ref index, "--lock-timeout-seconds", out string? timeout))
            {
                if (!int.TryParse(
                        timeout,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out lockTimeoutSeconds)
                    || lockTimeoutSeconds is < 0 or > 600)
                {
                    error = "--lock-timeout-seconds must be an integer from 0 through 600.";
                    return false;
                }

                continue;
            }

            if (IsHelp(argument))
            {
                return false;
            }

            error = $"Unknown option '{argument}'.";
            return false;
        }

        if (command == MigratorCommand.Validate && dryRun)
        {
            error = "--dry-run is only valid with migrate or seed-demo; validate is always read-only.";
            return false;
        }

        options = new CliOptions(command, dryRun, connectionString, lockTimeoutSeconds);
        return true;
    }

    public static string Usage =>
        """
        Sharpbill database compatibility migrator

        Usage:
          Sharpbill.Migrator validate [--connection <connection-string>]
          Sharpbill.Migrator migrate [--dry-run] [--connection <connection-string>]
                                      [--lock-timeout-seconds <0-600>]
          Sharpbill.Migrator seed-demo [--dry-run] [--connection <connection-string>]

        seed-demo requires APP_ENV=local or SHARPBILL_ALLOW_DEMO_SEED=true.

        Connection configuration, in precedence order:
          1. --connection
          2. DB_HOST/DB_PORT/DB_NAME/DB_USER/DB_PASSWORD (or SHARPBILL_DB_* aliases)
          3. ConnectionStrings__Sharpbill or SHARPBILL_DATABASE_CONNECTION

        Transport configuration uses the API's DB_REQUIRE_TLS and DB_TLS_CA_PATH contract.
        Production requires certificate and hostname verification (SslMode=VerifyFull).

        Exit codes:
           0 success                         2 invalid usage
          10 connection failed              20 empty database (validate only)
          21 unsupported Alembic revision   22 schema/seed validation failed
          23 partial database               24 demo seed refused
          30 migration execution failed
          31 migration lock unavailable
        """;

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        out string? value)
    {
        string argument = args[index];
        string prefix = optionName + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = argument[prefix.Length..];
            return true;
        }

        if (!string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }

        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            return true;
        }

        index++;
        value = args[index];
        return true;
    }

    private static bool IsHelp(string argument)
    {
        return argument is "-h" or "--help" or "help";
    }
}
