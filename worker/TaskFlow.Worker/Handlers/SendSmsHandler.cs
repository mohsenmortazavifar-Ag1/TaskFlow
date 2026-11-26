using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;
using TaskFlow.Worker.Services;

namespace TaskFlow.Worker.Handlers;

/// <summary>
/// Sends each SmsMessage at most once from this application. The durable SubmissionState
/// is claimed before the external HTTP call. Ambiguous provider outcomes are never retried.
/// </summary>
public sealed class SendSmsHandler(
    JobRepository jobs,
    TenantRegistryRepository tenantRegistry,
    TenantSqlConnectionFactory tenantConnections,
    TenantDatabaseLock tenantLock,
    ISmsGateway gateway,
    IOptions<SmsDeliveryOptions> deliveryOptions,
    ILogger<SendSmsHandler> logger) : IJobHandler
{
    public string JobType => "SendSms";

    public async Task HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        if (!payload.RootElement.TryGetProperty("smsMessageId", out var idElement) ||
            !idElement.TryGetInt64(out var smsMessageId) || smsMessageId <= 0)
            throw new InvalidOperationException("SendSms payload must contain a positive smsMessageId.");

        long? reminderOccurrenceId = null;
        if (payload.RootElement.TryGetProperty("reminderOccurrenceId", out var occurrenceNode))
        {
            if (!occurrenceNode.TryGetInt64(out var parsedOccurrenceId) || parsedOccurrenceId <= 0)
                throw new InvalidOperationException("SendSms reminderOccurrenceId must be a positive integer when supplied.");
            reminderOccurrenceId = parsedOccurrenceId;
        }

        var tenantId = job.TenantId
            ?? throw new InvalidOperationException("SMS jobs require TenantId.");

        await using var connection = await jobs.OpenConnectionAsync(cancellationToken);

        long tenantDatabaseId;
        await using (var tenantIdCommand = new SqlCommand("""
            SELECT TenantDatabaseId FROM dbo.SmsMessages
            WHERE SmsMessageId = @SmsMessageId AND TenantId = @TenantId;
            """, connection))
        {
            tenantIdCommand.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
            tenantIdCommand.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenantId;
            var value = await tenantIdCommand.ExecuteScalarAsync(cancellationToken);
            if (value is null || value is DBNull)
                throw new InvalidOperationException($"SMS {smsMessageId} has no owning TenantDatabaseId. Apply migration 009 and republish the job.");
            tenantDatabaseId = Convert.ToInt64(value);
        }

        var tenant = await tenantRegistry.GetActiveDatabaseAsync(tenantId, tenantDatabaseId, cancellationToken)
            ?? throw new InvalidOperationException($"Active TenantDatabaseId {tenantDatabaseId} was not found for TenantId {tenantId}.");
        await using var tenantConnection = await tenantConnections.OpenAsync(tenant, cancellationToken);
        await using var tenantLease = await tenantLock.AcquireAsync(
            tenantConnection, tenantDatabaseId, $"job:{job.JobId}", cancellationToken);

        // A reminder can be cancelled after its ExecuteReminder job created the SMS job.
        // Recheck the schedule immediately before claiming the SMS for provider submission.
        if (reminderOccurrenceId.HasValue)
        {
            await using var reminderCheck = new SqlCommand("""
                SELECT CASE WHEN EXISTS
                (
                    SELECT 1
                    FROM notification.ReminderOccurrences o
                    INNER JOIN notification.ReminderSchedules s
                        ON s.ReminderScheduleId = o.ReminderScheduleId
                    WHERE o.ReminderOccurrenceId = @OccurrenceId
                      AND o.Status = 2
                      AND o.ScheduleVersion = s.ScheduleVersion
                      AND s.IsEnabled = 1
                ) THEN 1 ELSE 0 END;
                """, tenantConnection);
            reminderCheck.Parameters.Add("@OccurrenceId", SqlDbType.BigInt).Value = reminderOccurrenceId.Value;
            if (Convert.ToInt32(await reminderCheck.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                await MarkReminderSmsCancelledAsync(
                    connection, smsMessageId, tenantId, reminderOccurrenceId.Value, cancellationToken);
                await MirrorSmsLogsToTenantAsync(connection, tenantConnection, smsMessageId, cancellationToken);
                logger.LogInformation(
                    "Skipping SMS message {SmsMessageId}: reminder occurrence {OccurrenceId} was cancelled or superseded before submission.",
                    smsMessageId, reminderOccurrenceId.Value);
                return;
            }
        }

        await using var read = new SqlCommand("""
            SELECT m.SmsMessageId, m.TenantId, m.Mobile, m.Message,
                   s.ProviderCode, s.SenderNumber, s.IsEnabled, m.SubmissionState
            FROM dbo.SmsMessages AS m
            LEFT JOIN dbo.TenantSmsSettings AS s ON s.TenantId = m.TenantId
            WHERE m.SmsMessageId = @SmsMessageId AND m.TenantId = @TenantId;
            """, connection);
        read.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
        read.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenantId;

        SmsMessage sms;
        bool enabled;
        byte submissionState;
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException(
                    $"SMS message {smsMessageId} was not found for tenant {tenantId}.");

            sms = new SmsMessage(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5));
            enabled = !reader.IsDBNull(6) && reader.GetBoolean(6);
            submissionState = reader.GetByte(7);
        }

        // Any non-pending state is a hard no-resend guard. Sending/Unknown requires
        // reconciliation; Accepted is handled only by the delivery checker.
        if (submissionState != 0)
        {
            await MirrorSmsLogsToTenantAsync(connection, tenantConnection, smsMessageId, cancellationToken);
            logger.LogWarning(
                "Skipping SendSms job {JobId}: SmsMessage {SmsMessageId} has SubmissionState {State}; no provider request will be repeated.",
                job.JobId, smsMessageId, submissionState);
            return;
        }

        if (!enabled)
            throw new InvalidOperationException(
                $"SMS is disabled or not configured for tenant {sms.TenantId}.");

        // Local validation is performed before changing Pending -> Sending.
        gateway.Validate(sms);

        await using (var claim = new SqlCommand("""
            UPDATE dbo.SmsMessages
            SET SubmissionState = 1,
                SubmissionStartedAt = SYSUTCDATETIME(),
                LastError = NULL
            WHERE SmsMessageId = @SmsMessageId
              AND TenantId = @TenantId
              AND SubmissionState = 0
              AND
              (
                  @ReminderOccurrenceId IS NULL
                  OR EXISTS
                  (
                      SELECT 1
                      FROM dbo.ReminderOccurrences o WITH (UPDLOCK, HOLDLOCK)
                      INNER JOIN dbo.ReminderSchedules s WITH (UPDLOCK, HOLDLOCK)
                          ON s.ReminderScheduleId = o.ReminderScheduleId
                      WHERE o.ReminderOccurrenceId = @ReminderOccurrenceId
                        AND o.Status = 2
                        AND o.ScheduleVersion = s.ScheduleVersion
                        AND s.IsEnabled = 1
                  )
              );
            """, connection))
        {
            claim.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = sms.SmsMessageId;
            claim.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = sms.TenantId;
            claim.Parameters.Add("@ReminderOccurrenceId", SqlDbType.BigInt).Value =
                (object?)reminderOccurrenceId ?? DBNull.Value;
            if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                if (reminderOccurrenceId.HasValue)
                    await MarkReminderSmsCancelledAsync(
                        connection, smsMessageId, tenantId, reminderOccurrenceId.Value, cancellationToken);
                logger.LogInformation(
                    "Skipping SMS message {SmsMessageId}: it could not be claimed, or its reminder was cancelled.",
                    smsMessageId);
                return;
            }
        }

        SmsSendResult result;
        try
        {
            result = await gateway.SendAsync(
                sms,
                job.IdempotencyKey ?? $"taskflow-sms-{sms.SmsMessageId}",
                cancellationToken);
        }
        catch (SmsGatewayConfigurationException)
        {
            await ResetToPendingAsync(connection, sms, cancellationToken);
            throw;
        }
        catch (SmsProviderRejectedException ex)
        {
            await PersistSubmissionFailureAsync(connection, sms, 3, "ProviderRejected", ex.Message);
            await MirrorSmsLogsToTenantAsync(connection, tenantConnection, smsMessageId, cancellationToken);
            logger.LogWarning(ex, "ParsGreen rejected SMS message {SmsMessageId}.", sms.SmsMessageId);
            return;
        }
        catch (SmsSubmissionOutcomeUnknownException ex)
        {
            await PersistSubmissionFailureAsync(connection, sms, 4, "SubmissionUnknown", ex.Message);
            await MirrorSmsLogsToTenantAsync(connection, tenantConnection, smsMessageId, cancellationToken);
            logger.LogError(ex,
                "SMS message {SmsMessageId} has an ambiguous provider outcome. Automatic resend is blocked.",
                sms.SmsMessageId);
            return;
        }

        var hasProviderId = !string.IsNullOrWhiteSpace(result.ProviderMessageId);
        var firstCheckDelay = Math.Clamp(deliveryOptions.Value.FirstCheckDelaySeconds, 30, 3600);
        var acceptedDescription = hasProviderId
            ? "ParsGreen accepted the SMS request. Delivery has not yet been confirmed."
            : "ParsGreen accepted the SMS request but returned no ReqID; delivery tracking is unavailable.";

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var update = new SqlCommand("""
                UPDATE dbo.SmsMessages
                SET Status = 1,
                    SubmissionState = 2,
                    ProviderMessageId = @ProviderMessageId,
                    AcceptedAt = SYSUTCDATETIME(),
                    DeliveryState = CASE WHEN @HasProviderId = 1 THEN 1 ELSE 5 END,
                    NextDeliveryCheckAt = CASE
                        WHEN @HasProviderId = 1 THEN DATEADD(SECOND, @FirstCheckDelaySeconds, SYSUTCDATETIME())
                        ELSE NULL
                    END,
                    LastError = CASE WHEN @HasProviderId = 1 THEN NULL
                        ELSE N'Accepted by ParsGreen, but ReqID was not returned; CheckDelivery cannot be called.' END
                WHERE SmsMessageId = @SmsMessageId
                  AND TenantId = @TenantId
                  AND SubmissionState = 1;

                IF @@ROWCOUNT <> 1
                    THROW 51001, 'SMS submission state changed before acceptance could be persisted.', 1;

                INSERT INTO dbo.SmsLogs
                    (SmsMessageId, ProviderCode, ProviderMessageId, EventType, Description)
                VALUES
                    (@SmsMessageId, @ProviderCode, @ProviderMessageId,
                     N'Accepted', @Description);
                """, connection, transaction);
            update.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = sms.SmsMessageId;
            update.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = sms.TenantId;
            update.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 100).Value =
                (object?)sms.ProviderCode ?? DBNull.Value;
            update.Parameters.Add("@ProviderMessageId", SqlDbType.NVarChar, 300).Value =
                (object?)result.ProviderMessageId ?? DBNull.Value;
            update.Parameters.Add("@HasProviderId", SqlDbType.Bit).Value = hasProviderId;
            update.Parameters.Add("@FirstCheckDelaySeconds", SqlDbType.Int).Value = firstCheckDelay;
            update.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value = acceptedDescription;
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await MirrorSmsLogsToTenantAsync(connection, tenantConnection, smsMessageId, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task MirrorSmsLogsToTenantAsync(
        SqlConnection centralConnection, SqlConnection tenantConnection, long smsMessageId, CancellationToken ct)
    {
        var logs = new List<(string Mobile, string Message, string EventType, string? ProviderCode,
            string? ProviderMessageId, string Description, DateTime? SentAt)>();
        await using (var read = new SqlCommand("""
            SELECT m.Mobile, m.Message, l.EventType, l.ProviderCode, l.ProviderMessageId,
                   l.Description, COALESCE(m.AcceptedAt, m.SubmissionStartedAt)
            FROM dbo.SmsLogs l
            INNER JOIN dbo.SmsMessages m ON m.SmsMessageId = l.SmsMessageId
            WHERE l.SmsMessageId = @SmsMessageId
            ORDER BY l.SmsLogId;
            """, centralConnection))
        {
            read.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                logs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }

        foreach (var log in logs)
        {
            await using var insert = new SqlCommand("""
                IF NOT EXISTS
                (
                    SELECT 1 FROM notification.SmsLogs
                    WHERE TaskFlowSmsMessageId = @SmsMessageId AND EventType = @EventType
                )
                INSERT notification.SmsLogs
                    (Mobile, Message, Status, ProviderMessageId, ErrorMessage, SentAt,
                     TaskFlowSmsMessageId, EventType, ProviderCode)
                VALUES
                    (@Mobile, @Message, @EventType, @ProviderMessageId,
                     CASE WHEN @EventType IN (N'ProviderRejected', N'SubmissionUnknown') THEN @Description ELSE NULL END,
                     @SentAt, @SmsMessageId, @EventType, @ProviderCode);
                """, tenantConnection);
            insert.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
            insert.Parameters.Add("@EventType", SqlDbType.NVarChar, 50).Value = log.EventType;
            insert.Parameters.Add("@Mobile", SqlDbType.NVarChar, 20).Value = log.Mobile;
            insert.Parameters.Add("@Message", SqlDbType.NVarChar, 2000).Value = log.Message;
            insert.Parameters.Add("@ProviderMessageId", SqlDbType.NVarChar, 300).Value = (object?)log.ProviderMessageId ?? DBNull.Value;
            insert.Parameters.Add("@Description", SqlDbType.NVarChar, 2000).Value = log.Description;
            insert.Parameters.Add("@SentAt", SqlDbType.DateTime2).Value = (object?)log.SentAt ?? DBNull.Value;
            insert.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 100).Value = (object?)log.ProviderCode ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task MarkReminderSmsCancelledAsync(
        SqlConnection connection,
        long smsMessageId,
        long tenantId,
        long reminderOccurrenceId,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            long? cancelledMessageId = null;
            string? providerCode = null;
            await using (var update = new SqlCommand("""
                UPDATE dbo.SmsMessages
                SET LastError = N'Reminder schedule was cancelled before provider submission.'
                OUTPUT INSERTED.SmsMessageId, INSERTED.ProviderCode
                WHERE SmsMessageId = @SmsMessageId
                  AND TenantId = @TenantId
                  AND SubmissionState = 0
                  AND AcceptedAt IS NULL;
                """, connection, transaction))
            {
                update.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
                update.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenantId;
                await using var reader = await update.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    cancelledMessageId = reader.GetInt64(0);
                    providerCode = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            if (cancelledMessageId.HasValue)
            {
                await using var log = new SqlCommand("""
                    INSERT INTO dbo.SmsLogs
                        (SmsMessageId, ProviderCode, EventType, Description)
                    VALUES
                        (@SmsMessageId, @ProviderCode, N'ReminderCancelled',
                         @Description);
                    """, connection, transaction);
                log.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = cancelledMessageId.Value;
                log.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 100).Value =
                    (object?)providerCode ?? DBNull.Value;
                log.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
                    $"Reminder occurrence {reminderOccurrenceId} was cancelled before SMS submission.";
                await log.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ResetToPendingAsync(
        SqlConnection connection,
        SmsMessage sms,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.SmsMessages
            SET SubmissionState = 0,
                SubmissionStartedAt = NULL
            WHERE SmsMessageId = @SmsMessageId
              AND TenantId = @TenantId
              AND SubmissionState = 1;
            """, connection);
        command.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = sms.SmsMessageId;
        command.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = sms.TenantId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PersistSubmissionFailureAsync(
        SqlConnection connection,
        SmsMessage sms,
        byte state,
        string eventType,
        string error)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await using var command = new SqlCommand("""
                UPDATE dbo.SmsMessages
                SET SubmissionState = @State,
                    DeliveryState = 5,
                    NextDeliveryCheckAt = NULL,
                    LastError = @Error
                WHERE SmsMessageId = @SmsMessageId
                  AND TenantId = @TenantId
                  AND SubmissionState = 1;

                INSERT INTO dbo.SmsLogs
                    (SmsMessageId, ProviderCode, ProviderMessageId, EventType, Description)
                SELECT @SmsMessageId, @ProviderCode, ProviderMessageId, @EventType, @Error
                FROM dbo.SmsMessages
                WHERE SmsMessageId = @SmsMessageId AND TenantId = @TenantId;
                """, connection, transaction);
            command.Parameters.Add("@State", SqlDbType.TinyInt).Value = state;
            command.Parameters.Add("@Error", SqlDbType.NVarChar, 2000).Value =
                error.Length > 2000 ? error[..2000] : error;
            command.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = sms.SmsMessageId;
            command.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = sms.TenantId;
            command.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 100).Value =
                (object?)sms.ProviderCode ?? DBNull.Value;
            command.Parameters.Add("@EventType", SqlDbType.NVarChar, 50).Value = eventType;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
