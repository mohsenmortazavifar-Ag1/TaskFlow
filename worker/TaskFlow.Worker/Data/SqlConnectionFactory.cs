using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;

namespace TaskFlow.Worker.Data;

public sealed class SqlConnectionFactory(IOptions<DatabaseOptions> options)
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(options.Value.ConnectionString)
        ? throw new InvalidOperationException("Database:ConnectionString is required.")
        : options.Value.ConnectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
