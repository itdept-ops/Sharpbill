using Dapper;
using MySqlConnector;

namespace Sharpbill.Infrastructure.Database;

public abstract class DapperRepository(DatabaseSession session)
{
    protected DatabaseSession Session { get; } = session;

    protected async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        await Session.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

    protected CommandDefinition Command(
        string sql,
        object? parameters,
        CancellationToken cancellationToken,
        CommandFlags flags = CommandFlags.Buffered) => new(
        sql,
        parameters,
        Session.Transaction,
        cancellationToken: cancellationToken,
        flags: flags);

    protected static CommandDefinition TransactionalCommand(
        string sql,
        object? parameters,
        MySqlTransaction transaction,
        CancellationToken cancellationToken,
        CommandFlags flags = CommandFlags.Buffered) => new(
        sql,
        parameters,
        transaction,
        cancellationToken: cancellationToken,
        flags: flags);
}
