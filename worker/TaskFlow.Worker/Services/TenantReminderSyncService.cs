using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Services;

/// <summary>
/// Projects reminder definitions from each tenant database into the central scheduler database.
/// The tenant database is the source of truth for definition changes; the central database owns
/// execution state (including the advanced NextRunAtUtc cursor after materialization).
/// </summary>
public sealed class TenantReminderSyncService(
    TenantRegistryRepository registry,
    TenantSqlConnectionFactory tenantConnections,
    SqlConnectionFactory centralConnections,
    IOptions<ReminderOptions> options,
    ILogger<TenantReminderSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.TenantSyncEnabled)
        {
            logger.LogInformation("Tenant Reminder synchronization is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(config.TenantSyncPollingIntervalSeconds, 5, 3600));
        using var timer = new PeriodicTimer(interval);
        logger.LogInformation("Tenant Reminder synchronization started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAllTenantsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tenant Reminder synchronization cycle failed.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SyncAllTenantsAsync(CancellationToken ct)
    {
        var tenants = await registry.GetActiveDatabasesAsync(ct);
        foreach (var tenant in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var count = await SyncTenantAsync(tenant, ct);
                if (count > 0)
                    logger.LogInformation(
                        "Synchronized {Count} reminder definition(s) for TenantId {TenantId} (TenantDatabaseId {TenantDatabaseId}).",
                        count, tenant.TenantId, tenant.TenantDatabaseId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unavailable or incompatible tenant must not block other tenants.
                logger.LogError(ex,
                    "Reminder synchronization failed for TenantId {TenantId}, TenantDatabaseId {TenantDatabaseId}, Database {DatabaseName}.",
                    tenant.TenantId, tenant.TenantDatabaseId, tenant.DatabaseName);
            }
        }
    }

    private async Task<int> SyncTenantAsync(TenantDatabase tenant, CancellationToken ct)
    {
        await using var source = await tenantConnections.OpenAsync(tenant, ct);
        var lastSyncedAt = await ReadWatermarkAsync(tenant.TenantDatabaseId, ct);
        var schedules = new List<TenantReminderSchedule>();

        await using (var read = new SqlCommand("""
            SELECT ReminderScheduleId, ReminderType, SourceEntity, SourceEntityId,
                   RuleJson, TimeZoneId, ScheduleVersion, IsEnabled, NextRunAtUtc, UpdatedAt
            FROM dbo.ReminderSchedules
            WHERE @LastSyncedAt IS NULL OR UpdatedAt >= @LastSyncedAt
            ORDER BY UpdatedAt, ReminderScheduleId;
            """, source) { CommandTimeout = 30 })
        {
            read.Parameters.Add("@LastSyncedAt", SqlDbType.DateTime2).Value =
                (object?)lastSyncedAt ?? DBNull.Value;
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                schedules.Add(new TenantReminderSchedule(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
                    reader.GetInt32(6), reader.GetBoolean(7),
                    reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    reader.GetDateTime(9)));
            }
        }

        if (schedules.Count == 0)
            return 0;

        await using var central = await centralConnections.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        try
        {
            foreach (var schedule in schedules)
            {
                await using var upsert = new SqlCommand("""
                    UPDATE dbo.ReminderSchedules WITH (UPDLOCK, SERIALIZABLE)
                    SET ReminderType = @ReminderType,
                        SourceEntity = @SourceEntity,
                        SourceEntityId = @SourceEntityId,
                        RuleJson = @RuleJson,
                        TimeZoneId = @TimeZoneId,
                        ScheduleVersion = @ScheduleVersion,
                        IsEnabled = @IsEnabled,
                        NextRunAtUtc = @NextRunAtUtc,
                        SourceUpdatedAt = @SourceUpdatedAt,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE TenantDatabaseId = @TenantDatabaseId
                      AND TenantReminderScheduleId = @TenantReminderScheduleId
                      AND SourceUpdatedAt < @SourceUpdatedAt;

                    IF @@ROWCOUNT = 0 AND NOT EXISTS
                    (
                        SELECT 1 FROM dbo.ReminderSchedules WITH (UPDLOCK, HOLDLOCK)
                        WHERE TenantDatabaseId = @TenantDatabaseId
                          AND TenantReminderScheduleId = @TenantReminderScheduleId
                    )
                    BEGIN
                        INSERT INTO dbo.ReminderSchedules
                            (TenantId, ReminderType, SourceEntity, SourceEntityId, RuleJson,
                             TimeZoneId, ScheduleVersion, IsEnabled, NextRunAtUtc,
                             TenantDatabaseId, TenantReminderScheduleId, SourceUpdatedAt)
                        VALUES
                            (@TenantId, @ReminderType, @SourceEntity, @SourceEntityId, @RuleJson,
                             @TimeZoneId, @ScheduleVersion, @IsEnabled, @NextRunAtUtc,
                             @TenantDatabaseId, @TenantReminderScheduleId, @SourceUpdatedAt);
                    END;
                    """, central, transaction);

                upsert.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenant.TenantId;
                upsert.Parameters.Add("@TenantDatabaseId", SqlDbType.BigInt).Value = tenant.TenantDatabaseId;
                upsert.Parameters.Add("@TenantReminderScheduleId", SqlDbType.BigInt).Value = schedule.ReminderScheduleId;
                upsert.Parameters.Add("@ReminderType", SqlDbType.NVarChar, 100).Value = schedule.ReminderType;
                upsert.Parameters.Add("@SourceEntity", SqlDbType.NVarChar, 100).Value = schedule.SourceEntity;
                upsert.Parameters.Add("@SourceEntityId", SqlDbType.BigInt).Value = schedule.SourceEntityId;
                upsert.Parameters.Add("@RuleJson", SqlDbType.NVarChar, -1).Value = schedule.RuleJson;
                upsert.Parameters.Add("@TimeZoneId", SqlDbType.NVarChar, 100).Value = schedule.TimeZoneId;
                upsert.Parameters.Add("@ScheduleVersion", SqlDbType.Int).Value = schedule.ScheduleVersion;
                upsert.Parameters.Add("@IsEnabled", SqlDbType.Bit).Value = schedule.IsEnabled;
                upsert.Parameters.Add("@NextRunAtUtc", SqlDbType.DateTime2).Value =
                    (object?)schedule.NextRunAtUtc ?? DBNull.Value;
                upsert.Parameters.Add("@SourceUpdatedAt", SqlDbType.DateTime2).Value = schedule.UpdatedAt;
                await upsert.ExecuteNonQueryAsync(ct);
            }

            var maxUpdatedAt = schedules.Max(x => x.UpdatedAt);
            await using (var watermark = new SqlCommand("""
                UPDATE dbo.TenantReminderSyncState
                SET LastSyncedAtUtc = CASE
                    WHEN LastSyncedAtUtc IS NULL OR LastSyncedAtUtc < @LastSyncedAtUtc
                    THEN @LastSyncedAtUtc ELSE LastSyncedAtUtc END,
                    LastSyncAtUtc = SYSUTCDATETIME(),
                    LastError = NULL
                WHERE TenantDatabaseId = @TenantDatabaseId;

                IF @@ROWCOUNT = 0
                    INSERT INTO dbo.TenantReminderSyncState
                        (TenantDatabaseId, LastSyncedAtUtc, LastSyncAtUtc)
                    VALUES (@TenantDatabaseId, @LastSyncedAtUtc, SYSUTCDATETIME());
                """, central, transaction))
            {
                watermark.Parameters.Add("@TenantDatabaseId", SqlDbType.BigInt).Value = tenant.TenantDatabaseId;
                watermark.Parameters.Add("@LastSyncedAtUtc", SqlDbType.DateTime2).Value = maxUpdatedAt;
                await watermark.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return schedules.Count;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private async Task<DateTime?> ReadWatermarkAsync(long tenantDatabaseId, CancellationToken ct)
    {
        await using var connection = await centralConnections.OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT LastSyncedAtUtc
            FROM dbo.TenantReminderSyncState
            WHERE TenantDatabaseId = @TenantDatabaseId;
            """, connection);
        command.Parameters.Add("@TenantDatabaseId", SqlDbType.BigInt).Value = tenantDatabaseId;
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : (DateTime)value;
    }

    private sealed record TenantReminderSchedule(
        long ReminderScheduleId, string ReminderType, string SourceEntity, long SourceEntityId,
        string RuleJson, string TimeZoneId, int ScheduleVersion, bool IsEnabled,
        DateTime? NextRunAtUtc, DateTime UpdatedAt);
}
