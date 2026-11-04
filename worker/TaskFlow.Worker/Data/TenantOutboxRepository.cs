using System.Data;
using Microsoft.Data.SqlClient;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Data;

public sealed class TenantOutboxRepository
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        SqlConnection connection, string workerId, int batchSize, int leaseSeconds, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.OutboxMessages
               SET Status = 3,
                   LastError = COALESCE(LastError, N'Maximum attempts reached after lease expiry.'),
                   LockedBy = NULL, LockedUntil = NULL
             WHERE Status = 1 AND LockedUntil < SYSUTCDATETIME()
               AND AttemptCount >= MaxAttempts;

            ;WITH candidates AS
            (
                SELECT TOP (@BatchSize) *
                FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status IN (0, 1)
                  AND (NextAttemptAt IS NULL OR NextAttemptAt <= SYSUTCDATETIME())
                  AND (Status = 0 OR LockedUntil IS NULL OR LockedUntil < SYSUTCDATETIME())
                  AND AttemptCount < MaxAttempts
                ORDER BY Priority DESC, CreatedAt ASC, OutboxId ASC
            )
            UPDATE candidates
               SET Status = 1,
                   LockedBy = @WorkerId,
                   LockedUntil = DATEADD(SECOND, @LeaseSeconds, SYSUTCDATETIME()),
                   AttemptCount = AttemptCount + 1
            OUTPUT INSERTED.OutboxId, INSERTED.EventId, INSERTED.EventType,
                   INSERTED.QueueName, INSERTED.JobType, INSERTED.Payload,
                   INSERTED.Priority, INSERTED.AttemptCount, INSERTED.MaxAttempts;
            """, connection);
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = Math.Clamp(batchSize, 1, 100);
        command.Parameters.Add("@WorkerId", SqlDbType.NVarChar, 150).Value = workerId;
        command.Parameters.Add("@LeaseSeconds", SqlDbType.Int).Value = Math.Clamp(leaseSeconds, 30, 1800);

        var messages = new List<OutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            messages.Add(new OutboxMessage(reader.GetInt64(0), reader.GetGuid(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetByte(6), reader.GetInt32(7), reader.GetInt32(8)));
        return messages;
    }

    public async Task MarkPublishedAsync(SqlConnection connection, long outboxId, string workerId, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.OutboxMessages
               SET Status = 2, ProcessedAt = SYSUTCDATETIME(),
                   LockedBy = NULL, LockedUntil = NULL, LastError = NULL
             WHERE OutboxId = @OutboxId AND Status = 1 AND LockedBy = @WorkerId;
            """, connection);
        command.Parameters.Add("@OutboxId", SqlDbType.BigInt).Value = outboxId;
        command.Parameters.Add("@WorkerId", SqlDbType.NVarChar, 150).Value = workerId;
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException($"Outbox lease lost for OutboxId={outboxId}.");
    }

    public async Task MarkFailedAsync(SqlConnection connection, OutboxMessage message,
        string workerId, Exception error, CancellationToken ct)
    {
        var errorText = error.ToString();
        if (errorText.Length > 2000) errorText = errorText[..2000];

        await using var command = new SqlCommand("""
            UPDATE dbo.OutboxMessages
               SET Status = CASE WHEN AttemptCount >= MaxAttempts THEN 3 ELSE 0 END,
                   NextAttemptAt = CASE
                       WHEN AttemptCount >= MaxAttempts THEN NULL
                       ELSE DATEADD(SECOND,
                           CASE WHEN AttemptCount >= 9 THEN 3600
                                ELSE CONVERT(int, POWER(CAST(2 AS int),
                                    CASE WHEN AttemptCount > 8 THEN 8 ELSE AttemptCount END) * 5) END,
                           SYSUTCDATETIME())
                   END,
                   LastError = @Error,
                   LockedBy = NULL, LockedUntil = NULL
             WHERE OutboxId = @OutboxId AND Status = 1 AND LockedBy = @WorkerId;
            """, connection);
        command.Parameters.Add("@OutboxId", SqlDbType.BigInt).Value = message.OutboxId;
        command.Parameters.Add("@WorkerId", SqlDbType.NVarChar, 150).Value = workerId;
        command.Parameters.Add("@Error", SqlDbType.NVarChar, 2000).Value = errorText;
        await command.ExecuteNonQueryAsync(ct);
    }
}
