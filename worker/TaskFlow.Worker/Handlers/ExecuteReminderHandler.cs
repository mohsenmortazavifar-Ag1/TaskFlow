using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Handlers;

/// <summary>
/// Validates a queued reminder against its current schedule version, then atomically
/// creates the SMS message and SMS queue job. It never calls the SMS provider directly.
/// </summary>
public sealed class ExecuteReminderHandler(JobRepository jobs) : IJobHandler
{
    public string JobType => "ExecuteReminder";

    public async Task HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!job.TenantId.HasValue)
            throw new InvalidOperationException("Reminder jobs require TenantId.");

        using var payload = JsonDocument.Parse(job.Payload);
        if (!payload.RootElement.TryGetProperty("reminderOccurrenceId", out var idNode) ||
            !idNode.TryGetInt64(out var occurrenceId) || occurrenceId <= 0)
            throw new InvalidOperationException("ExecuteReminder payload requires a positive reminderOccurrenceId.");

        await using var connection = await jobs.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            long scheduleId;
            int occurrenceVersion;
            byte occurrenceStatus;
            long tenantId;
            string reminderType;
            string sourceEntity;
            long sourceEntityId;
            string timeZoneId;
            int currentVersion;
            bool enabled;
            DateTime dueAtUtc;
            string ruleJson;

            await using (var read = new SqlCommand("""
                SELECT o.ReminderScheduleId, o.ScheduleVersion, o.Status,
                       s.TenantId, s.ReminderType, s.SourceEntity, s.SourceEntityId,
                       s.TimeZoneId, s.ScheduleVersion, s.IsEnabled, o.DueAtUtc, s.RuleJson
                FROM dbo.ReminderOccurrences o WITH (UPDLOCK, HOLDLOCK)
                INNER JOIN dbo.ReminderSchedules s WITH (UPDLOCK, HOLDLOCK)
                    ON s.ReminderScheduleId = o.ReminderScheduleId
                WHERE o.ReminderOccurrenceId = @OccurrenceId;
                """, connection, transaction))
            {
                read.Parameters.Add("@OccurrenceId", SqlDbType.BigInt).Value = occurrenceId;
                await using var reader = await read.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException($"Reminder occurrence {occurrenceId} was not found.");

                scheduleId = reader.GetInt64(0);
                occurrenceVersion = reader.GetInt32(1);
                occurrenceStatus = reader.GetByte(2);
                tenantId = reader.GetInt64(3);
                reminderType = reader.GetString(4);
                sourceEntity = reader.GetString(5);
                sourceEntityId = reader.GetInt64(6);
                timeZoneId = reader.GetString(7);
                currentVersion = reader.GetInt32(8);
                enabled = reader.GetBoolean(9);
                dueAtUtc = reader.GetDateTime(10);
                ruleJson = reader.GetString(11);
            }

            if (tenantId != job.TenantId.Value)
                throw new InvalidOperationException("Reminder occurrence tenant does not match the claimed job tenant.");

            // Already finalized occurrences are idempotent no-ops.
            if (occurrenceStatus is 2 or 3)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            // Status 0 is not yet dispatched; the scheduler sets it to 1 when it creates the job.
            if (occurrenceStatus != 1)
                throw new InvalidOperationException($"Reminder occurrence {occurrenceId} has unexpected status {occurrenceStatus}.");

            // A changed or disabled schedule invalidates all older queued occurrences.
            if (!enabled || currentVersion != occurrenceVersion)
            {
                await using var skip = new SqlCommand("""
                    UPDATE dbo.ReminderOccurrences
                    SET Status = 3, ExecutedAt = SYSUTCDATETIME()
                    WHERE ReminderOccurrenceId = @OccurrenceId AND Status = 1;
                    """, connection, transaction);
                skip.Parameters.Add("@OccurrenceId", SqlDbType.BigInt).Value = occurrenceId;
                await skip.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var rule = ParseRule(ruleJson);
            var message = rule.Message
                .Replace("{reminderType}", reminderType, StringComparison.Ordinal)
                .Replace("{sourceEntity}", sourceEntity, StringComparison.Ordinal)
                .Replace("{sourceEntityId}", sourceEntityId.ToString(), StringComparison.Ordinal)
                .Replace("{dueAtUtc}", DateTime.SpecifyKind(dueAtUtc, DateTimeKind.Utc).ToString("O"), StringComparison.Ordinal);

            await using var insertSms = new SqlCommand("""
                INSERT INTO dbo.SmsMessages(TenantId, Mobile, Message, ProviderCode, Status)
                OUTPUT INSERTED.SmsMessageId
                VALUES(@TenantId, @Mobile, @Message, @ProviderCode, 0);
                """, connection, transaction);
            insertSms.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenantId;
            insertSms.Parameters.Add("@Mobile", SqlDbType.NVarChar, 20).Value = rule.Mobile;
            insertSms.Parameters.Add("@Message", SqlDbType.NVarChar, 2000).Value = message;
            insertSms.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 50).Value =
                (object?)rule.ProviderCode ?? DBNull.Value;
            var smsMessageId = Convert.ToInt64(await insertSms.ExecuteScalarAsync(cancellationToken));

            var smsKey = $"reminder-occurrence:{occurrenceId}";
            long smsJobId;
            await using (var insertJob = new SqlCommand("""
                INSERT INTO dbo.Jobs
                    (TenantId, QueueName, JobType, Payload, Priority, ScheduledAt, IdempotencyKey)
                OUTPUT INSERTED.JobId
                VALUES
                    (@TenantId, N'sms', N'SendSms', @Payload, 10, SYSUTCDATETIME(), @IdempotencyKey);
                """, connection, transaction))
            {
                insertJob.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenantId;
                insertJob.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value =
                    JsonSerializer.Serialize(new { smsMessageId });
                insertJob.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, 200).Value = smsKey;
                smsJobId = Convert.ToInt64(await insertJob.ExecuteScalarAsync(cancellationToken));
            }

            await using (var link = new SqlCommand("""
                UPDATE dbo.SmsMessages SET JobId = @JobId WHERE SmsMessageId = @SmsMessageId;
                UPDATE dbo.ReminderOccurrences
                SET Status = 2, ExecutedAt = SYSUTCDATETIME()
                WHERE ReminderOccurrenceId = @OccurrenceId AND Status = 1;
                """, connection, transaction))
            {
                link.Parameters.Add("@JobId", SqlDbType.BigInt).Value = smsJobId;
                link.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
                link.Parameters.Add("@OccurrenceId", SqlDbType.BigInt).Value = occurrenceId;
                await link.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private static ReminderRule ParseRule(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var mobile = root.TryGetProperty("mobile", out var mobileNode) ? mobileNode.GetString() : null;
        var message = root.TryGetProperty("message", out var messageNode) ? messageNode.GetString() : null;
        var provider = root.TryGetProperty("providerCode", out var providerNode) ? providerNode.GetString() : null;
        int? intervalValue = root.TryGetProperty("intervalValue", out var valueNode) && valueNode.TryGetInt32(out var value)
            ? value : null;
        var intervalUnit = root.TryGetProperty("intervalUnit", out var unitNode) ? unitNode.GetString() : null;
        var localTime = root.TryGetProperty("localTime", out var timeNode) ? timeNode.GetString() : null;

        if (string.IsNullOrWhiteSpace(mobile) || mobile.Length > 20)
            throw new InvalidOperationException("Reminder RuleJson requires a mobile value (maximum 20 characters).");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 2000)
            throw new InvalidOperationException("Reminder RuleJson requires a message (maximum 2000 characters).");
        if (intervalValue.HasValue &&
            (intervalValue.Value < 1 || intervalValue.Value > 3650 ||
             intervalUnit is not ("day" or "month" or "year")))
            throw new InvalidOperationException("Reminder recurrence must use intervalValue 1..3650 and intervalUnit day, month or year.");
        if (!intervalValue.HasValue && intervalUnit is not null)
            throw new InvalidOperationException("intervalUnit cannot be supplied without intervalValue.");
        if (localTime is not null &&
            !TimeOnly.TryParseExact(localTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Reminder localTime must use HH:mm format.");

        return new ReminderRule(mobile!, message!, provider, intervalValue, intervalUnit, localTime);
    }
}
