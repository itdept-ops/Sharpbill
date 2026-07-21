namespace Sharpbill.Migrator;

internal enum ExitCode
{
    Success = 0,
    Usage = 2,
    ConnectionFailed = 10,
    EmptyDatabaseRequiresMigration = 20,
    UnsupportedAlembicRevision = 21,
    SchemaValidationFailed = 22,
    PartialDatabase = 23,
    DemoSeedRefused = 24,
    MigrationFailed = 30,
    LockUnavailable = 31,
}
