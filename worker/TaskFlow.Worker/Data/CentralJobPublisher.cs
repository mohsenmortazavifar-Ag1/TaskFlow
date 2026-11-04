using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Data;

/// <summary>Transfers tenant outbox events into the central queue with an idempotent key.</summary>
public sealed class CentralJobPublisher(SqlConnectionFactory centralConnections)
{
    public async Task PublishAsync(long tenantId, OutboxMessage message, CancellationToken ct)
    {
        await using var connection = await centralConnections.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        try
        {
            var idempotencyKey = message.EventId.ToString("N");
            await using (var exists = new SqlCommand("""
                SELECT JobId
                FROM dbo.Jobs WITH (UPDLOCK, HOLDLOCK)
                WHERE TenantId = @TenantId AND IdempotencyKey = @IdempotencyKey;
                """, connection, transaction))
            {
                exists.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenantId;
                exists.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, 200).Value = idempotencyKey;
                if (await exists.ExecuteScalarAsync(ct) is not null)
                {
                    await transaction.CommitAsync(ct);
                    return;
                }
            }

            var jobPayload = message.Payload;
            long? smsMessageId = null;
            if (string.Equals(message.JobType, "SendSms", StringComparison.OrdinalIgnoreCase))
            {
                using var json = JsonDocument.Parse(message.Payload);
                var root = json.RootElement;
                if (!root.TryGetProperty("mobile", out var mobileNode) ||
                    !root.TryGetProperty("message", out var messageNode))
                    throw new InvalidOperationException("SendSms outbox payload requires mobile and message fields.");

                var mobile = mobileNode.GetString();
                var text = messageNode.GetString();
                if (string.IsNullOrWhiteSpace(mobile) || string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("SendSms mobile and message must not be empty.");

                var providerCode = root.TryGetProperty("providerCode", out var providerNode)
                    ? providerNode.GetString() : null;

                await using var insertSms = new SqlCommand("""
                    INSERT INTO dbo.SmsMessages(TenantId, Mobile, Message, ProviderCode, Status)
                    OUTPUT INSERTED.SmsMessageId
                    VALUES(@TenantId, @Mobile, @Message, @ProviderCode, 0);
                    """, connection, transaction);
                insertSms.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenantId;
                insertSms.Parameters.Add("@Mobile", SqlDbType.NVarChar, 20).Value = mobile;
                insertSms.Parameters.Add("@Message", SqlDbType.NVarChar, 2000).Value = text;
                insertSms.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 50).Value =
                    (object?)providerCode ?? DBNull.Value;
                smsMessageId = Convert.ToInt64(await insertSms.ExecuteScalarAsync(ct));
                jobPayload = JsonSerializer.Serialize(new { smsMessageId = smsMessageId.Value });
            }

            await using var insertJob = new SqlCommand("""
                INSERT INTO dbo.Jobs
                    (TenantId, QueueName, JobType, Payload, Priority, ScheduledAt, IdempotencyKey)
                OUTPUT INSERTED.JobId
                VALUES
                    (@TenantId, @QueueName, @JobType, @Payload, @Priority, SYSUTCDATETIME(), @IdempotencyKey);
                """, connection, transaction);
            insertJob.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenantId;
            insertJob.Parameters.Add("@QueueName", SqlDbType.NVarChar, 50).Value = message.QueueName;
            insertJob.Parameters.Add("@JobType", SqlDbType.NVarChar, 100).Value = message.JobType;
            insertJob.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = jobPayload;
            insertJob.Parameters.Add("@Priority", SqlDbType.TinyInt).Value = message.Priority;
            insertJob.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, 200).Value = idempotencyKey;
            var jobId = Convert.ToInt64(await insertJob.ExecuteScalarAsync(ct));

            if (smsMessageId.HasValue)
            {
                await using var link = new SqlCommand(
                    "UPDATE dbo.SmsMessages SET JobId = @JobId WHERE SmsMessageId = @SmsMessageId;",
                    connection, transaction);
                link.Parameters.Add("@JobId", SqlDbType.BigInt).Value = jobId;
                link.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId.Value;
                await link.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }
}
