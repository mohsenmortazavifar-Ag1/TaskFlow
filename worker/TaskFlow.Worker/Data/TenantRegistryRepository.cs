using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Data;

public sealed class TenantRegistryRepository(IOptions<TenantRegistryOptions> options)
{
    public async Task<IReadOnlyList<TenantDatabase>> GetActiveDatabasesAsync(CancellationToken ct)
    {
        var cs = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("TenantRegistry:ConnectionString is required.");

        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT TenantDatabaseId, TenantId, ServerName, DatabaseName,
                   SqlLoginName, SqlPasswordEncrypted, SchemaVersion
            FROM dbo.TenantDatabases
            WHERE IsActive = 1
            ORDER BY TenantDatabaseId;
            """, connection) { CommandTimeout = 30 };

        var result = new List<TenantDatabase>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new TenantDatabase(reader.GetInt64(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return result;
    }
}
