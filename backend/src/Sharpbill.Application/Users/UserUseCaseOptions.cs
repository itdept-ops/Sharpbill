namespace Sharpbill.Application.Users;

public sealed record UserUseCaseOptions(
    int ExportMaxBytes,
    int PreciseLocationHours);
