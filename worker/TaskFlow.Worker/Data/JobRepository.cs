using System.Data;
using Microsoft.Data.SqlClient;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Data;

public sealed class JobRepository(SqlConnectionFactory connections)
{
    public async Task<IReadOnlyList<QueueDefinition>> GetEnabledQueuesAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT QueueName, IsEnabled, MaxConcurrency, VisibilityTimeoutSeconds, PollingIntervalSeconds
            FROM dbo.JobQueues
            WHERE IsEnabled = 1
            ORDER BY SortOrder, QueueId;
            """, connection);
        var result = new List<QueueDefinition>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new QueueDefinition(reader.GetString(0), reader.GetBoolean(1),
                reader.GetInt16(2), reader.GetInt32(3), reader.GetInt32(4)));
        return result;
    }

    public async Task<ClaimedJob?> ClaimAsync(string queueName, string workerId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = new SqlCommand("dbo.ClaimJobs", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@QueueName", SqlDbType.NVarChar, 50).Value = queueName;
        command.Parameters.Add("@WorkerId", SqlDbType.NVarChar, 150).Value = workerId;
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = 1;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return null;

        var tenantOrdinal = reader.GetOrdinal("TenantId");
        var idempotencyOrdinal = reader.GetOrdinal("IdempotencyKey");
        return new ClaimedJob(
            reader.GetInt64(reader.GetOrdinal("JobId")),
            reader.IsDBNull(tenantOrdinal) ? null : reader.GetInt64(tenantOrdinal),
            reader.GetString(reader.GetOrdinal("QueueName")),
            reader.GetString(reader.GetOrdinal("JobType")),
            reader.GetString(reader.GetOrdinal("Payload")),
            reader.GetByte(reader.GetOrdinal("AttemptCount")),
            reader.GetByte(reader.GetOrdinal("MaxAttempts")),
            reader.IsDBNull(idempotencyOrdinal) ? null : reader.GetString(idempotencyOrdinal));
    }

    public async Task CompleteAsync(long jobId, string workerId, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = new SqlCommand("dbo.CompleteJob", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@JobId", SqlDbType.BigInt).Value = jobId;
        command.Parameters.Add("@WorkerId", SqlDbType.NVarChar, 150).Value = workerId;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task FailAsync(long jobId, string workerId, string error, int retryDelaySeconds, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = new SqlCommand("dbo.FailJob", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@JobId", SqlDbType.BigInt).Value = jobId;
        command.Parameters.Add("@WorkerId", SqlDbType.NVarChar, 150).Value = workerId;
        command.Parameters.Add("@Error", SqlDbType.NVarChar, 2000).Value = error.Length > 2000 ? error[..2000] : error;
        command.Parameters.Add("@RetryDelaySeconds", SqlDbType.Int).Value = retryDelaySeconds;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct) => await connections.OpenAsync(ct);
}
