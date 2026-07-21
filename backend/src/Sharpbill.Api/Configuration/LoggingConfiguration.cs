namespace Sharpbill.Api.Configuration;

internal static class LoggingConfiguration
{
    public static LogLevel ParseMinimumLevel(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? configured = configuration["LOG_LEVEL"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return LogLevel.Information;
        }

        string normalized = configured.Trim().ToUpperInvariant();
        return normalized switch
        {
            "TRACE" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "INFO" or "INFORMATION" => LogLevel.Information,
            "WARN" or "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            "CRITICAL" or "FATAL" => LogLevel.Critical,
            "NONE" => LogLevel.None,
            _ => throw new InvalidOperationException(
                "LOG_LEVEL must be TRACE, DEBUG, INFO, WARN, ERROR, CRITICAL, or NONE."),
        };
    }
}
