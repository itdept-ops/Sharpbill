using Dapper;
using MySqlConnector;

namespace Sharpbill.Migrator;

internal sealed class DatabaseMigrationLock : IAsyncDisposable
{
    private readonly MySqlConnection _connection;
    private readonly string _lockName;
    private bool _held;

    private DatabaseMigrationLock(MySqlConnection connection, string lockName)
    {
        _connection = connection;
        _lockName = lockName;
        _held = true;
    }

    public static async Task<DatabaseMigrationLock?> TryAcquireAsync(
        MySqlConnection connection,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        const string nameSql =
            "SELECT CONCAT('sharpbill-migrator:', LEFT(SHA2(DATABASE(), 256), 40))";
        string? lockName = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            nameSql,
            cancellationToken: cancellationToken));
        if (string.IsNullOrEmpty(lockName))
        {
            throw new InvalidOperationException("MySQL did not return a migration lock name.");
        }
        int? acquired = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT GET_LOCK(@LockName, @TimeoutSeconds)",
            new { LockName = lockName, TimeoutSeconds = timeoutSeconds },
            cancellationToken: cancellationToken));
        return acquired == 1 ? new DatabaseMigrationLock(connection, lockName) : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_held || _connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        _held = false;
        await _connection.ExecuteScalarAsync<int?>(
            "SELECT RELEASE_LOCK(@LockName)",
            new { LockName = _lockName });
    }
}
