using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;

namespace TaskFlow.Worker.Services;

/// <summary>
/// ParsGreen delivery checks are deliberately capped at two requests per SMS.
/// 41 = delivered, 50 = pending, 0 = undelivered (based on observed provider tests).
/// Unknown values are retained and are never interpreted as success.
/// </summary>
public sealed class SmsDeliveryPollingService(
    JobRepository jobs,
    ISmsGateway gateway,
    IOptions<SmsDeliveryOptions> options,
    ILogger<SmsDeliveryPollingService> logger) : BackgroundService
{
    private sealed record DueSms(long SmsMessageId, string ProviderMessageId, byte CheckCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.Enabled)
        {
            logger.LogInformation("SMS delivery polling is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(config.PollingIntervalSeconds, 5, 300));
        logger.LogInformation("SMS delivery polling started. Maximum checks per message: 2.");
        await LogDatabaseSessionDiagnosticsAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await MarkStaleSubmissionsForReviewAsync(stoppingToken);
                await FinalizeExpiredSecondChecksAsync(stoppingToken);
                var due = await ClaimDueMessagesAsync(stoppingToken);
                foreach (var sms in due)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await CheckOneAsync(sms, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SMS delivery polling cycle failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task MarkStaleSubmissionsForReviewAsync(CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE dbo.SmsMessages
            SET SubmissionState = 4,
                DeliveryState = 5,
                NextDeliveryCheckAt = NULL,
                LastError = COALESCE(LastError,
                    N'Worker did not persist the provider response after starting submission; manual reconciliation is required.')
            WHERE SubmissionState = 1
              AND SubmissionStartedAt < DATEADD(MINUTE, -@TimeoutMinutes, SYSUTCDATETIME());
            """, connection);
        command.Parameters.Add("@TimeoutMinutes", SqlDbType.Int).Value =
            Math.Clamp(options.Value.SendingTimeoutMinutes, 1, 60);
        var count = await command.ExecuteNonQueryAsync(ct);
        if (count > 0)
            logger.LogError("{Count} SMS submissions were moved to NeedsReview; automatic resend is blocked.", count);
    }

    private async Task FinalizeExpiredSecondChecksAsync(CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE dbo.SmsMessages
            SET DeliveryState = 4,
                NextDeliveryCheckAt = NULL,
                LastError = COALESCE(LastError,
                    N'The second CheckDelivery request did not produce a persisted result before its lease expired.')
            WHERE SubmissionState = 2
              AND DeliveryState = 1
              AND DeliveryCheckCount >= 2
              AND NextDeliveryCheckAt <= SYSUTCDATETIME();

            DECLARE @Finalized int = @@ROWCOUNT;
            IF @Finalized > 0
                INSERT INTO dbo.SmsLogs
                    (SmsMessageId, ProviderCode, ProviderMessageId, EventType, Description)
                SELECT SmsMessageId, ProviderCode, ProviderMessageId,
                       N'DeliveryCheckUnconfirmed',
                       N'The second CheckDelivery result was not persisted before timeout. No further checks will be made.'
                FROM dbo.SmsMessages
                WHERE SubmissionState = 2
                  AND DeliveryState = 4
                  AND DeliveryCheckCount >= 2
                  AND LastError = N'The second CheckDelivery request did not produce a persisted result before its lease expired.';
            """, connection);
        var count = await command.ExecuteNonQueryAsync(ct);
        if (count > 0)
            logger.LogWarning("{Count} SMS messages were marked unconfirmed after the second-check lease expired.", count);
    }

    private async Task LogDatabaseSessionDiagnosticsAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await jobs.OpenConnectionAsync(ct);
            await using var command = new SqlCommand("""
                SELECT
                    CAST(@@SPID AS int) AS SessionId,
                    CAST(DB_NAME() AS nvarchar(128)) AS DatabaseName,
                    CAST(session.transaction_isolation_level AS int) AS IsolationLevel,
                    database_info.is_read_committed_snapshot_on AS IsReadCommittedSnapshotOn,
                    CAST(database_info.snapshot_isolation_state_desc AS nvarchar(60)) AS SnapshotIsolationState
                FROM sys.dm_exec_sessions AS session
                INNER JOIN sys.databases AS database_info
                    ON database_info.database_id = DB_ID()
                WHERE session.session_id = @@SPID;
                """, connection);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct))
            {
                logger.LogWarning("Could not read SQL Server session diagnostics for SMS delivery polling.");
                return;
            }

            var sessionId = reader.GetInt32(0);
            var databaseName = reader.GetString(1);
            var isolationLevel = reader.GetInt32(2);
            var isReadCommittedSnapshotOn = reader.GetBoolean(3);
            var snapshotIsolationState = reader.GetString(4);
            var isolationName = isolationLevel switch
            {
                1 => "READ UNCOMMITTED",
                2 => "READ COMMITTED",
                3 => "REPEATABLE READ",
                4 => "SERIALIZABLE",
                5 => "SNAPSHOT",
                _ => $"UNKNOWN ({isolationLevel})"
            };

            logger.LogInformation(
                "SMS delivery SQL session diagnostics: SessionId={SessionId}, Database={DatabaseName}, Isolation={IsolationLevel} ({IsolationName}), READ_COMMITTED_SNAPSHOT={ReadCommittedSnapshotOn}, SnapshotIsolationState={SnapshotIsolationState}.",
                sessionId, databaseName, isolationLevel, isolationName,
                isReadCommittedSnapshotOn, snapshotIsolationState);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Diagnostics must never prevent the hosted service from starting.
            logger.LogWarning(ex, "Could not collect SQL session diagnostics for SMS delivery polling.");
        }
    }

    private async Task<IReadOnlyList<DueSms>> ClaimDueMessagesAsync(CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            -- READPAST requires a locking READ COMMITTED/REPEATABLE READ session.
            -- Force READ COMMITTED for this standalone claim operation. READCOMMITTEDLOCK
            -- is also required when the database has READ_COMMITTED_SNAPSHOT enabled.
            SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

            ;WITH Due AS
            (
                SELECT TOP (@BatchSize) *
                FROM dbo.SmsMessages WITH (UPDLOCK, READPAST, ROWLOCK, READCOMMITTEDLOCK)
                WHERE SubmissionState = 2
                  AND DeliveryState = 1
                  AND ProviderMessageId IS NOT NULL
                  AND DeliveryCheckCount < 2
                  AND NextDeliveryCheckAt <= SYSUTCDATETIME()
                ORDER BY NextDeliveryCheckAt, SmsMessageId
            )
            UPDATE Due
            SET DeliveryCheckCount = DeliveryCheckCount + 1,
                LastDeliveryCheckAt = SYSUTCDATETIME(),
                NextDeliveryCheckAt = DATEADD(SECOND, @ClaimLeaseSeconds, SYSUTCDATETIME())
            OUTPUT inserted.SmsMessageId, inserted.ProviderMessageId, inserted.DeliveryCheckCount;
            """, connection);
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value =
            Math.Clamp(options.Value.BatchSize, 1, 100);
        command.Parameters.Add("@ClaimLeaseSeconds", SqlDbType.Int).Value =
            Math.Clamp(options.Value.SubsequentCheckDelaySeconds, 30, 3600);

        var result = new List<DueSms>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new DueSms(reader.GetInt64(0), reader.GetString(1), reader.GetByte(2)));
        return result;
    }

    private async Task CheckOneAsync(DueSms sms, CancellationToken ct)
    {
        SmsDeliveryResult result;
        try
        {
            result = await gateway.CheckDeliveryAsync(sms.ProviderMessageId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Claim lease remains. If this was the second check, it is finalized as unconfirmed.
            throw;
        }
        catch (Exception ex)
        {
            await PersistCheckFailureAsync(sms, ex.Message, ct);
            logger.LogWarning(ex, "CheckDelivery failed for SMS {SmsMessageId}, check {CheckCount}/2.",
                sms.SmsMessageId, sms.CheckCount);
            return;
        }

        if (!result.IsSuccess)
        {
            var reason = !string.IsNullOrWhiteSpace(result.Error) ? result.Error : result.Message;
            await PersistCheckFailureAsync(sms,
                $"CheckDelivery rejected the query (code {result.ResponseCode}): {reason ?? "No provider message"}", ct);
            return;
        }

        switch (result.DeliveryStatus)
        {
            case 41:
                await PersistFinalAsync(sms, 2, 41, "Delivered",
                    "ParsGreen reports that the SMS was delivered.", true, null, ct);
                break;
            case 50 when sms.CheckCount < 2:
                await ScheduleSecondCheckAsync(sms, 50,
                    "ParsGreen reports that delivery is still in progress.", ct);
                break;
            case 50:
                await PersistFinalAsync(sms, 4, 50, "DeliveryUnconfirmed",
                    "Delivery remained in progress after the maximum of two checks.", false,
                    "ParsGreen returned DeliveryStatus=50 after two checks.", ct);
                break;
            case 0:
                await PersistFinalAsync(sms, 3, 0, "Undelivered",
                    "ParsGreen returned DeliveryStatus=0. No automatic resend will be attempted.",
                    false, "ParsGreen returned DeliveryStatus=0; delivery was not confirmed.", ct);
                break;
            default:
                await PersistFinalAsync(sms, 5, result.DeliveryStatus, "DeliveryUnknown",
                    $"Unmapped ParsGreen DeliveryStatus={result.DeliveryStatus}. No further automatic checks will be made.",
                    false, $"Unmapped ParsGreen DeliveryStatus={result.DeliveryStatus}.", ct);
                break;
        }
    }

    private async Task PersistCheckFailureAsync(DueSms sms, string error, CancellationToken ct)
    {
        if (sms.CheckCount < 2)
        {
            await ScheduleSecondCheckAsync(sms, null,
                $"First CheckDelivery request failed: {error}", ct);
            return;
        }

        await PersistFinalAsync(sms, 4, null, "DeliveryCheckUnconfirmed",
            $"Delivery could not be confirmed after two CheckDelivery requests: {error}",
            false, $"Delivery check limit reached: {error}", ct);
    }

    private async Task ScheduleSecondCheckAsync(DueSms sms, int? providerStatus, string description, CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE dbo.SmsMessages
            SET ProviderDeliveryStatus = COALESCE(@ProviderStatus, ProviderDeliveryStatus),
                NextDeliveryCheckAt = DATEADD(SECOND, @DelaySeconds, SYSUTCDATETIME()),
                LastError = @LastError
            WHERE SmsMessageId = @SmsMessageId
              AND SubmissionState = 2
              AND DeliveryState = 1
              AND DeliveryCheckCount = @CheckCount;
            """, connection);
        command.Parameters.Add("@ProviderStatus", SqlDbType.Int).Value = (object?)providerStatus ?? DBNull.Value;
        command.Parameters.Add("@DelaySeconds", SqlDbType.Int).Value =
            Math.Clamp(options.Value.SubsequentCheckDelaySeconds, 30, 3600);
        command.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value =
            description.Length > 2000 ? description[..2000] : description;
        command.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = sms.SmsMessageId;
        command.Parameters.Add("@CheckCount", SqlDbType.TinyInt).Value = sms.CheckCount;
        await command.ExecuteNonQueryAsync(ct);
        await InsertLogAsync(sms.SmsMessageId, "DeliveryPending", description, ct);
    }

    private async Task PersistFinalAsync(
        DueSms sms, byte deliveryState, int? providerStatus, string eventType,
        string description, bool delivered, string? lastError, CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            await using var command = new SqlCommand("""
                UPDATE dbo.SmsMessages
                SET DeliveryState = @DeliveryState,
                    ProviderDeliveryStatus = @ProviderStatus,
                    DeliveredAt = CASE WHEN @Delivered = 1 THEN COALESCE(DeliveredAt, SYSUTCDATETIME()) ELSE DeliveredAt END,
                    NextDeliveryCheckAt = NULL,
                    LastError = @LastError
                WHERE SmsMessageId = @SmsMessageId
                  AND SubmissionState = 2
                  AND DeliveryState = 1
                  AND DeliveryCheckCount = @CheckCount;

                IF @@ROWCOUNT = 1
                    INSERT INTO dbo.SmsLogs
                        (SmsMessageId, ProviderCode, ProviderMessageId, EventType, Description)
                    SELECT SmsMessageId, ProviderCode, ProviderMessageId, @EventType, @Description
                    FROM dbo.SmsMessages WHERE SmsMessageId = @SmsMessageId;
                """, connection, transaction);
            command.Parameters.Add("@DeliveryState", SqlDbType.TinyInt).Value = deliveryState;
            command.Parameters.Add("@ProviderStatus", SqlDbType.Int).Value = (object?)providerStatus ?? DBNull.Value;
            command.Parameters.Add("@Delivered", SqlDbType.Bit).Value = delivered;
            command.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value = (object?)lastError ?? DBNull.Value;
            command.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = sms.SmsMessageId;
            command.Parameters.Add("@CheckCount", SqlDbType.TinyInt).Value = sms.CheckCount;
            command.Parameters.Add("@EventType", SqlDbType.NVarChar, 50).Value = eventType;
            command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
                description.Length > 1000 ? description[..1000] : description;
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task InsertLogAsync(long smsMessageId, string eventType, string description, CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            INSERT INTO dbo.SmsLogs(SmsMessageId, ProviderCode, ProviderMessageId, EventType, Description)
            SELECT SmsMessageId, ProviderCode, ProviderMessageId, @EventType, @Description
            FROM dbo.SmsMessages WHERE SmsMessageId = @SmsMessageId;
            """, connection);
        command.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
        command.Parameters.Add("@EventType", SqlDbType.NVarChar, 50).Value = eventType;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value =
            description.Length > 1000 ? description[..1000] : description;
        await command.ExecuteNonQueryAsync(ct);
    }
}
