using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Database;

public sealed class MySqlConnectionFactory : IDatabaseConnectionFactory, IAsyncDisposable
{
    private readonly MySqlDataSource _dataSource;
    private readonly TimeSpan _poolAcquisitionTimeout;

    public MySqlConnectionFactory(IOptions<SharpbillOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        DatabaseOptions database = options.Value.Database;
        _poolAcquisitionTimeout = TimeSpan.FromSeconds(database.PoolTimeoutSeconds);
        MySqlConnectionStringBuilder connectionString = new()
        {
            Server = database.Host,
            Port = database.Port,
            Database = database.Name,
            UserID = database.User,
            Password = database.Password,
            CharacterSet = "utf8mb4",
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = database.PoolSize + database.MaxOverflow,
            ConnectionTimeout = database.ConnectTimeoutSeconds,
            DefaultCommandTimeout = Math.Max(database.ReadTimeoutSeconds, database.WriteTimeoutSeconds),
            ConnectionLifeTime = database.PoolRecycleSeconds,
            ConnectionReset = true,
            IgnoreCommandTransaction = false,
            AllowUserVariables = false,
            SslMode = database.RequireTls ? MySqlSslMode.VerifyFull : MySqlSslMode.Disabled,
        };
        if (database.RequireTls)
        {
            connectionString.SslCa = database.TlsCaPath;
        }

        _dataSource = new MySqlDataSourceBuilder(connectionString.ConnectionString).Build();
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async ValueTask<MySqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_poolAcquisitionTimeout);
        MySqlConnection connection;
        try
        {
            connection = await _dataSource.OpenConnectionAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DatabaseConnectionTimeoutException(_poolAcquisitionTimeout, exception);
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SET SESSION time_zone = '+00:00'",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

public sealed class DatabaseConnectionTimeoutException(TimeSpan timeout, Exception innerException)
    : TimeoutException($"Database pool acquisition exceeded {timeout.TotalSeconds} seconds.", innerException);
