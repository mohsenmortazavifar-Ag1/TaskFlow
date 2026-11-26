using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;

namespace TaskFlow.Worker.Services;

/// <summary>
/// Retryable central-to-tenant SMS log replication. The central SmsLogs row is the
/// durable delivery outbox; TenantSyncedAt is advanced only after the tenant insert succeeds.
/// </summary>
public sealed class TenantSmsLogSyncService(
    JobRepository jobs,
    TenantRegistryRepository registry,
    TenantSqlConnectionFactory tenantConnections,
    TenantDatabaseLock tenantLock,
    WorkerHeartbeatService heartbeat,
    ILogger<TenantSmsLogSyncService> logger) : BackgroundService
{
    private sealed record PendingLog(long SmsLogId, long SmsMessageId, long TenantId, long TenantDatabaseId,
        string Mobile, string Message, string EventType, string? ProviderCode, string? ProviderMessageId,
        string Description, int? ProviderStatus, DateTime? SentAt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Tenant SMS log synchronization started.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Tenant SMS log synchronization cycle failed."); }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task SyncBatchAsync(CancellationToken ct)
    {
        var pending = new List<PendingLog>();
        await using (var central = await jobs.OpenConnectionAsync(ct))
        await using (var command = new SqlCommand("""
            SELECT TOP (250)
                l.SmsLogId, m.SmsMessageId, m.TenantId, m.TenantDatabaseId,
                m.Mobile, m.Message, l.EventType, l.ProviderCode, l.ProviderMessageId,
                l.Description, m.ProviderDeliveryStatus, m.AcceptedAt
            FROM dbo.SmsLogs l
            INNER JOIN dbo.SmsMessages m ON m.SmsMessageId = l.SmsMessageId
            WHERE l.TenantSyncedAt IS NULL
              AND (l.TenantSyncNextAttemptAt IS NULL OR l.TenantSyncNextAttemptAt <= SYSUTCDATETIME())
              AND m.TenantDatabaseId IS NOT NULL
            ORDER BY l.SmsLogId;
            """, central))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                pending.Add(new PendingLog(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? "" : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : reader.GetDateTime(11)));
        }

        foreach (var group in pending.GroupBy(x => (x.TenantId, x.TenantDatabaseId)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tenant = await registry.GetActiveDatabaseAsync(group.Key.TenantId, group.Key.TenantDatabaseId, ct);
                if (tenant is null)
                {
                    await DeferTenantLogsAsync(group.Key.TenantDatabaseId, 5,
                        "Tenant database is not active in MasterRepair.", ct);
                    logger.LogWarning("Deferred SMS log sync: tenant database {TenantDatabaseId} is not active.", group.Key.TenantDatabaseId);
                    continue;
                }

                await using var tenantConnection = await tenantConnections.OpenAsync(tenant, ct);
                await using var lease = await tenantLock.AcquireAsync(
                    tenantConnection, tenant.TenantDatabaseId, heartbeat.WorkerId, ct);

                foreach (var log in group)
                {
                    await using (var insert = new SqlCommand("""
                        IF NOT EXISTS
                        (
                            SELECT 1 FROM notification.SmsLogs
                            WHERE TaskFlowSmsLogId = @SmsLogId
                        )
                        INSERT notification.SmsLogs
                            (Mobile, Message, Status, ProviderMessageId, ErrorMessage, SentAt,
                             TaskFlowSmsMessageId, TaskFlowSmsLogId, EventType, ProviderCode, ProviderStatus)
                        VALUES
                            (@Mobile, @Message, @EventType, @ProviderMessageId,
                             CASE WHEN @EventType IN (N'ProviderRejected', N'SubmissionUnknown',
                                 N'DeliveryUnknown', N'DeliveryUnconfirmed', N'Undelivered') THEN @Description ELSE NULL END,
                             @SentAt, @SmsMessageId, @SmsLogId, @EventType, @ProviderCode, @ProviderStatus);
                        """, tenantConnection))
                    {
                        insert.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = log.SmsMessageId;
                        insert.Parameters.Add("@SmsLogId", SqlDbType.BigInt).Value = log.SmsLogId;
                        insert.Parameters.Add("@EventType", SqlDbType.NVarChar, 50).Value = log.EventType;
                        insert.Parameters.Add("@Mobile", SqlDbType.NVarChar, 20).Value = log.Mobile;
                        insert.Parameters.Add("@Message", SqlDbType.NVarChar, 2000).Value = log.Message;
                        insert.Parameters.Add("@ProviderMessageId", SqlDbType.NVarChar, 300).Value = (object?)log.ProviderMessageId ?? DBNull.Value;
                        insert.Parameters.Add("@Description", SqlDbType.NVarChar, 2000).Value = log.Description.Length > 2000 ? log.Description[..2000] : log.Description;
                        insert.Parameters.Add("@SentAt", SqlDbType.DateTime2).Value = (object?)log.SentAt ?? DBNull.Value;
                        insert.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 100).Value = (object?)log.ProviderCode ?? DBNull.Value;
                        insert.Parameters.Add("@ProviderStatus", SqlDbType.Int).Value = (object?)log.ProviderStatus ?? DBNull.Value;
                        await insert.ExecuteNonQueryAsync(ct);
                    }

                    await using var centralMarkConnection = await jobs.OpenConnectionAsync(ct);
                    await using var mark = new SqlCommand("""
                        UPDATE dbo.SmsLogs
                        SET TenantSyncedAt = SYSUTCDATETIME(),
                            TenantSyncNextAttemptAt = NULL,
                            TenantSyncLastError = NULL
                        WHERE SmsLogId = @SmsLogId AND TenantSyncedAt IS NULL;
                        """, centralMarkConnection);
                    mark.Parameters.Add("@SmsLogId", SqlDbType.BigInt).Value = log.SmsLogId;
                    await mark.ExecuteNonQueryAsync(ct);
                }
                logger.LogInformation("Synchronized {Count} SMS log event(s) to TenantDatabaseId {TenantDatabaseId}.",
                    group.Count(), group.Key.TenantDatabaseId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                try
                {
                    await DeferTenantLogsAsync(group.Key.TenantDatabaseId, 1, ex.Message, ct);
                }
                catch (Exception deferError)
                {
                    logger.LogError(deferError,
                        "Could not defer failed SMS log sync rows for TenantDatabaseId {TenantDatabaseId}.",
                        group.Key.TenantDatabaseId);
                }
                logger.LogError(ex, "SMS log synchronization failed for TenantDatabaseId {TenantDatabaseId}; rows were deferred for retry.",
                    group.Key.TenantDatabaseId);
            }
        }
    }

    private async Task DeferTenantLogsAsync(long tenantDatabaseId, int delayMinutes, string error, CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE l
            SET TenantSyncNextAttemptAt = DATEADD(MINUTE, @DelayMinutes, SYSUTCDATETIME()),
                TenantSyncLastError = @Error
            FROM dbo.SmsLogs l
            INNER JOIN dbo.SmsMessages m ON m.SmsMessageId = l.SmsMessageId
            WHERE m.TenantDatabaseId = @TenantDatabaseId
              AND l.TenantSyncedAt IS NULL;
            """, connection);
        command.Parameters.Add("@TenantDatabaseId", SqlDbType.BigInt).Value = tenantDatabaseId;
        command.Parameters.Add("@DelayMinutes", SqlDbType.Int).Value = Math.Clamp(delayMinutes, 1, 60);
        command.Parameters.Add("@Error", SqlDbType.NVarChar, 1000).Value =
            error.Length > 1000 ? error[..1000] : error;
        await command.ExecuteNonQueryAsync(ct);
    }
}
