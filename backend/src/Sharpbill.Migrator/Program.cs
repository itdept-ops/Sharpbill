using MySqlConnector;

namespace Sharpbill.Migrator;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var reporter = new ConsoleReporter();
        if (!CliOptions.TryParse(args, out CliOptions? options, out string? error))
        {
            if (error is not null)
            {
                reporter.Error("SBM0002", error);
            }

            Console.Out.WriteLine(CliOptions.Usage);
            return (int)ExitCode.Usage;
        }

        try
        {
            string connectionString = ConnectionStringResolver.Resolve(options!);
            var migrator = new DatabaseMigrator(reporter);
            return (int)await migrator.RunAsync(options!, connectionString, CancellationToken.None);
        }
        catch (ConfigurationException exception)
        {
            reporter.Error("SBM0003", exception.Message);
            return (int)ExitCode.Usage;
        }
        catch (MySqlException exception)
        {
            reporter.Error(
                "SBM0010",
                $"Unable to connect to or query the selected MySQL database: {exception.Message}");
            return (int)ExitCode.ConnectionFailed;
        }
        catch (Exception exception)
        {
            reporter.Error("SBM0030", $"Migration failed safely: {exception.Message}");
            return (int)ExitCode.MigrationFailed;
        }
    }
}
