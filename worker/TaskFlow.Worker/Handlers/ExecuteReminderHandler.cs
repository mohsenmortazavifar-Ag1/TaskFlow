using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Handlers;

/// <summary>
/// Tenant DB owns the reminder state. Central TaskFlow owns SMS queue records.
/// Central insertion is idempotent; after it commits, tenant occurrence linking is safely retryable.
/// </summary>
public sealed class ExecuteReminderHandler(
    JobRepository jobs,
    TenantRegistryRepository tenantRegistry,
    TenantSqlConnectionFactory tenantConnections,
    TenantDatabaseLock tenantLock,
    ILogger<ExecuteReminderHandler> logger) : IJobHandler
{
    public string JobType => "ExecuteReminder";

    public async Task HandleAsync(ClaimedJob job, CancellationToken ct)
    {
        if (!job.TenantId.HasValue)
            throw new InvalidOperationException("Reminder jobs require TenantId.");

        using var payload = JsonDocument.Parse(job.Payload);
        var root = payload.RootElement;
        var occurrenceId = RequiredInt64(root, "reminderOccurrenceId");
        var tenantDatabaseId = RequiredInt64(root, "tenantDatabaseId");
        var tenant = await tenantRegistry.GetActiveDatabaseAsync(job.TenantId.Value, tenantDatabaseId, ct)
            ?? throw new InvalidOperationException($"Active TenantDatabaseId {tenantDatabaseId} was not found for TenantId {job.TenantId}.");

        await using var tenantConnection = await tenantConnections.OpenAsync(tenant, ct);
        await using var tenantLease = await tenantLock.AcquireAsync(
            tenantConnection, tenant.TenantDatabaseId, $"job:{job.JobId}", ct);

        string mobile;
        string message;
        string? providerCode;
        long scheduleId;
        byte occurrenceStatus;
        bool scheduleEnabled;
        int occurrenceVersion;
        int scheduleVersion;
        await using (var read = new SqlCommand("""
            SELECT o.ReminderScheduleId, o.Status, o.ScheduleVersion,
                   s.ScheduleVersion, s.IsEnabled,
                   JSON_VALUE(s.RuleJson, '$.mobile'),
                   JSON_VALUE(s.RuleJson, '$.message'),
                   JSON_VALUE(s.RuleJson, '$.providerCode')
            FROM notification.ReminderOccurrences o
            INNER JOIN notification.ReminderSchedules s
                ON s.ReminderScheduleId = o.ReminderScheduleId
            WHERE o.ReminderOccurrenceId = @OccurrenceId;
            """, tenantConnection))
        {
            read.Parameters.Add("@OccurrenceId", SqlDbType.BigInt).Value = occurrenceId;
            await using var reader = await read.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Tenant reminder occurrence {occurrenceId} was not found.");
            scheduleId = reader.GetInt64(0);
            occurrenceStatus = reader.GetByte(1);
            occurrenceVersion = reader.GetInt32(2);
            scheduleVersion = reader.GetInt32(3);
            scheduleEnabled = reader.GetBoolean(4);
            mobile = reader.IsDBNull(5) ? "" : reader.GetString(5);
            message = reader.IsDBNull(6) ? "" : reader.GetString(6);
            providerCode = reader.IsDBNull(7) ? null : reader.GetString(7);
        }

        if (occurrenceStatus is 2 or 3 or 4)
            return;
        if (occurrenceStatus != 1)
            throw new InvalidOperationException($"Occurrence {occurrenceId} must be ExecutionRequested (1), current status is {occurrenceStatus}.");

        if (!scheduleEnabled || occurrenceVersion != scheduleVersion)
        {
            await UpdateOccurrenceAsync(tenantConnection, occurrenceId, 3, null,
                "Schedule disabled or version superseded before SMS queueing.", ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(mobile) || mobile.Length > 20 ||
            string.IsNullOrWhiteSpace(message) || message.Length > 2000)
            throw new InvalidOperationException($"Reminder schedule {scheduleId} has invalid mobile or message template.");

        var idempotencyKey = $"reminder-occurrence:{tenantDatabaseId}:{occurrenceId}";
        long smsMessageId;
        long smsJobId;

        await using (var central = await jobs.OpenConnectionAsync(ct))
        await using (var tx = (SqlTransaction)await central.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            try
            {
                await using (var existing = new SqlCommand("""
                    SELECT SmsMessageId, JobId
                    FROM dbo.SmsMessages WITH (UPDLOCK, HOLDLOCK)
                    WHERE TenantId = @TenantId AND IdempotencyKey = @IdempotencyKey;
                    """, central, tx))
                {
                    existing.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenant.TenantId;
                    existing.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, 200).Value = idempotencyKey;
                    await using var reader = await existing.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
                    if (await reader.ReadAsync(ct))
                    {
                        smsMessageId = reader.GetInt64(0);
                        smsJobId = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                    }
                    else
                    {
                        smsMessageId = 0;
                        smsJobId = 0;
                    }
                }

                if (smsMessageId == 0)
                {
                    await using var insertSms = new SqlCommand("""
                        INSERT dbo.SmsMessages
                            (TenantId, TenantDatabaseId, Mobile, Message, ProviderCode, Status,
                             SubmissionState, IdempotencyKey)
                        OUTPUT INSERTED.SmsMessageId
                        VALUES
                            (@TenantId, @TenantDatabaseId, @Mobile, @Message, @ProviderCode, 0, 0, @IdempotencyKey);
                        """, central, tx);
                    insertSms.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenant.TenantId;
                    insertSms.Parameters.Add("@TenantDatabaseId", SqlDbType.BigInt).Value = tenantDatabaseId;
                    insertSms.Parameters.Add("@Mobile", SqlDbType.NVarChar, 20).Value = mobile;
                    insertSms.Parameters.Add("@Message", SqlDbType.NVarChar, 2000).Value = message;
                    insertSms.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 100).Value = (object?)providerCode ?? DBNull.Value;
                    insertSms.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, 200).Value = idempotencyKey;
                    smsMessageId = Convert.ToInt64(await insertSms.ExecuteScalarAsync(ct));
                }

                if (smsJobId == 0)
                {
                    await using var findJob = new SqlCommand("""
                        SELECT JobId FROM dbo.Jobs WITH (UPDLOCK, HOLDLOCK)
                        WHERE TenantId = @TenantId AND IdempotencyKey = @JobKey;
                        """, central, tx);
                    var jobKey = $"send-sms:{idempotencyKey}";
                    findJob.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenant.TenantId;
                    findJob.Parameters.Add("@JobKey", SqlDbType.NVarChar, 200).Value = jobKey;
                    var found = await findJob.ExecuteScalarAsync(ct);
                    if (found is not null)
                        smsJobId = Convert.ToInt64(found);
                    else
                    {
                        await using var insertJob = new SqlCommand("""
                            INSERT dbo.Jobs
                                (TenantId, QueueName, JobType, Payload, Priority, ScheduledAt, IdempotencyKey)
                            OUTPUT INSERTED.JobId
                            VALUES
                                (@TenantId, N'sms', N'SendSms', @Payload, 10, SYSUTCDATETIME(), @JobKey);
                            """, central, tx);
                        insertJob.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenant.TenantId;
                        insertJob.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value =
                            JsonSerializer.Serialize(new { smsMessageId, reminderOccurrenceId = occurrenceId, tenantDatabaseId });
                        insertJob.Parameters.Add("@JobKey", SqlDbType.NVarChar, 200).Value = jobKey;
                        smsJobId = Convert.ToInt64(await insertJob.ExecuteScalarAsync(ct));
                    }
                    await using var link = new SqlCommand(
                        "UPDATE dbo.SmsMessages SET JobId=@JobId WHERE SmsMessageId=@SmsMessageId;",
                        central, tx);
                    link.Parameters.Add("@JobId", SqlDbType.BigInt).Value = smsJobId;
                    link.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
                    await link.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None); } catch { }
                throw;
            }
        }

        await UpdateOccurrenceAsync(tenantConnection, occurrenceId, 2, smsMessageId, null, ct);
        logger.LogInformation("Reminder occurrence {OccurrenceId} queued as central SMS {SmsMessageId} for TenantDatabaseId {TenantDatabaseId}.",
            occurrenceId, smsMessageId, tenantDatabaseId);
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node) || !node.TryGetInt64(out var value) || value <= 0)
            throw new InvalidOperationException($"Reminder job payload requires a positive {name}.");
        return value;
    }

    private static async Task UpdateOccurrenceAsync(
        SqlConnection connection, long occurrenceId, byte status, long? smsMessageId, string? error, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            UPDATE notification.ReminderOccurrences
            SET Status=@Status,
                SmsMessageId=COALESCE(@SmsMessageId, SmsMessageId),
                ExecutedAtUtc=CASE WHEN @Status IN (2,3,4) THEN SYSUTCDATETIME() ELSE ExecutedAtUtc END,
                LastError=@Error
            WHERE ReminderOccurrenceId=@OccurrenceId;
            """, connection);
        command.Parameters.Add("@Status", SqlDbType.TinyInt).Value = status;
        command.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = (object?)smsMessageId ?? DBNull.Value;
        command.Parameters.Add("@Error", SqlDbType.NVarChar, 1000).Value = (object?)error ?? DBNull.Value;
        command.Parameters.Add("@OccurrenceId", SqlDbType.BigInt).Value = occurrenceId;
        await command.ExecuteNonQueryAsync(ct);
    }
}
