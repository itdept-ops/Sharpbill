using MySqlConnector;

namespace Sharpbill.Infrastructure.Database;

public interface IDatabaseConnectionFactory
{
    ValueTask<MySqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}
