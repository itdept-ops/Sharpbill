using System.Data;
using MySqlConnector;
using Sharpbill.Application.Abstractions;

namespace Sharpbill.Infrastructure.Database;

public sealed class DatabaseSession(IDatabaseConnectionFactory connectionFactory) : IUnitOfWork
{
    private MySqlConnection? _connection;
    private MySqlTransaction? _transaction;
    private bool _completed;

    public MySqlConnection Connection => _connection
        ?? throw new InvalidOperationException("The database session has not been opened.");

    public MySqlTransaction? Transaction => _transaction;

    /// <summary>
    /// Executes a repository operation in the current unit-of-work transaction, or creates a
    /// short transaction when the operation is required to be atomic on its own.
    /// </summary>
    public async Task<T> ExecuteTransactionallyAsync<T>(
        Func<MySqlConnection, MySqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        MySqlConnection connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (_transaction is not null)
        {
            return await operation(connection, _transaction, cancellationToken).ConfigureAwait(false);
        }

        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            T result = await operation(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<MySqlConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        _connection ??= await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return _connection;
    }

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A database transaction is already active.");
        }

        MySqlConnection connection = await GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken).ConfigureAwait(false);
        _completed = false;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        MySqlTransaction transaction = _transaction
            ?? throw new InvalidOperationException("No database transaction is active.");
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _transaction = null;
            }
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            return;
        }

        MySqlTransaction transaction = _transaction;
        try
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _transaction = null;
            }
        }
    }

    /// <summary>
    /// Releases pooled database resources once application work is materialized, allowing a
    /// potentially slow HTTP response body to transmit without pinning a pool lease.
    /// </summary>
    public async ValueTask ReleaseConnectionAsync()
    {
        try
        {
            if (_transaction is not null)
            {
                MySqlTransaction transaction = _transaction;
                _transaction = null;
                try
                {
                    if (!_completed)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (_connection is not null)
            {
                MySqlConnection connection = _connection;
                _connection = null;
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseConnectionAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
