using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Services;

/// <summary>
/// Projects reminder definitions from each tenant database into the central scheduler database.
/// Tenant databases own reminder definitions. The central database owns execution state,
/// including the NextRunAtUtc cursor after a schedule has been materialized.
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

        var interval = TimeSpan.FromSeconds(
            Math.Clamp(config.TenantSyncPollingIntervalSeconds, 5, 3600));

        using var timer = new PeriodicTimer(interval);
        logger.LogInformation(
            "Tenant Reminder synchronization started. Polling interval: {IntervalSeconds}s.",
            interval.TotalSeconds);

        // Run immediately; the first PeriodicTimer tick otherwise waits one full interval.
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
                var result = await SyncTenantAsync(tenant, ct);

                if (result.Inserted > 0 || result.Updated > 0)
                {
                    logger.LogInformation(
                        "Tenant Reminder sync applied changes. TenantId={TenantId}, TenantDatabaseId={TenantDatabaseId}, Read={Read}, Inserted={Inserted}, Updated={Updated}, Unchanged={Unchanged}, StaleSkipped={StaleSkipped}.",
                        tenant.TenantId,
                        tenant.TenantDatabaseId,
                        result.Read,
                        result.Inserted,
                        result.Updated,
                        result.Unchanged,
                        result.StaleSkipped);
                }
                else
                {
                    logger.LogDebug(
                        "Tenant Reminder sync completed with no central changes. TenantId={TenantId}, TenantDatabaseId={TenantDatabaseId}, Read={Read}, Unchanged={Unchanged}, StaleSkipped={StaleSkipped}.",
                        tenant.TenantId,
                        tenant.TenantDatabaseId,
                        result.Read,
                        result.Unchanged,
                        result.StaleSkipped);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await TryRecordFailureAsync(tenant.TenantDatabaseId, ex, ct);

                // One unavailable or incompatible tenant must not block other tenants.
                logger.LogError(
                    ex,
                    "Reminder synchronization failed for TenantId {TenantId}, TenantDatabaseId {TenantDatabaseId}, Database {DatabaseName}.",
                    tenant.TenantId,
                    tenant.TenantDatabaseId,
                    tenant.DatabaseName);
            }
        }
    }

    private async Task<SyncResult> SyncTenantAsync(TenantDatabase tenant, CancellationToken ct)
    {
        await using var source = await tenantConnections.OpenAsync(tenant, ct);
        var schedules = new List<TenantReminderSchedule>();

        // Deliberately read the complete current definition set.
        // LastSyncedAtUtc is retained as an operational watermark, not used as a filter:
        // a timestamp-only incremental cursor can repeatedly select boundary rows and can
        // miss concurrent updates when multiple rows share the same UpdatedAt value.
        // A full scan is deterministic for the current schedule volume; only changed rows
        // are written to the central database.
        await using (var read = new SqlCommand("""
            SELECT ReminderScheduleId, ReminderType, SourceEntity, SourceEntityId,
                   RuleJson, TimeZoneId, ScheduleVersion, IsEnabled, NextRunAtUtc, UpdatedAt
            FROM dbo.ReminderSchedules
            ORDER BY UpdatedAt, ReminderScheduleId;
            """, source) { CommandTimeout = 30 })
        {
            await using var reader = await read.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                schedules.Add(new TenantReminderSchedule(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetBoolean(7),
                    reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    reader.GetDateTime(9)));
            }
        }

        await using var central = await centralConnections.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await central.BeginTransactionAsync(
            IsolationLevel.Serializable, ct);

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        var staleSkipped = 0;

        try
        {
            foreach (var schedule in schedules)
            {
                ct.ThrowIfCancellationRequested();

                var action = await UpsertScheduleAsync(
                    central, transaction, tenant, schedule, ct);

                switch (action)
                {
                    case UpsertAction.Inserted:
                        inserted++;
                        break;
                    case UpsertAction.Updated:
                        updated++;
                        break;
                    case UpsertAction.Unchanged:
                        unchanged++;
                        break;
                    case UpsertAction.StaleSkipped:
                        staleSkipped++;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown reminder upsert action: {(byte)action}.");
                }
            }

            DateTime? maxUpdatedAt = schedules.Count == 0
                ? null
                : schedules.Max(x => x.UpdatedAt);

            await UpdateWatermarkAsync(
                central,
                transaction,
                tenant.TenantDatabaseId,
                maxUpdatedAt,
                ct);

            await transaction.CommitAsync(ct);

            return new SyncResult(
                schedules.Count,
                inserted,
                updated,
                unchanged,
                staleSkipped);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Preserve the original synchronization exception.
            }

            throw;
        }
    }

    private static async Task<UpsertAction> UpsertScheduleAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TenantDatabase tenant,
        TenantReminderSchedule schedule,
        CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            DECLARE @Found bit = 0;
            DECLARE @ExistingSourceUpdatedAt datetime2(7);
            DECLARE @ExistingScheduleVersion int;
            DECLARE @Action tinyint = 0;

            SELECT
                @Found = 1,
                @ExistingSourceUpdatedAt = SourceUpdatedAt,
                @ExistingScheduleVersion = ScheduleVersion
            FROM dbo.ReminderSchedules WITH (UPDLOCK, HOLDLOCK)
            WHERE TenantDatabaseId = @TenantDatabaseId
              AND TenantReminderScheduleId = @TenantReminderScheduleId;

            IF @Found = 0
            BEGIN
                INSERT INTO dbo.ReminderSchedules
                    (TenantId, ReminderType, SourceEntity, SourceEntityId, RuleJson,
                     TimeZoneId, ScheduleVersion, IsEnabled, NextRunAtUtc,
                     TenantDatabaseId, TenantReminderScheduleId, SourceUpdatedAt)
                VALUES
                    (@TenantId, @ReminderType, @SourceEntity, @SourceEntityId, @RuleJson,
                     @TimeZoneId, @ScheduleVersion, @IsEnabled, @NextRunAtUtc,
                     @TenantDatabaseId, @TenantReminderScheduleId, @SourceUpdatedAt);

                SET @Action = 1; -- Inserted
            END
            ELSE IF @ExistingSourceUpdatedAt IS NOT NULL
                 AND @ExistingSourceUpdatedAt > @SourceUpdatedAt
            BEGIN
                SET @Action = 3; -- Stale source snapshot; do not overwrite newer central data.
            END
            ELSE IF EXISTS
            (
                SELECT 1
                FROM dbo.ReminderSchedules
                WHERE TenantDatabaseId = @TenantDatabaseId
                  AND TenantReminderScheduleId = @TenantReminderScheduleId
                  AND
                  (
                      CONVERT(varbinary(max), ReminderType) <> CONVERT(varbinary(max), @ReminderType)
                      OR CONVERT(varbinary(max), SourceEntity) <> CONVERT(varbinary(max), @SourceEntity)
                      OR SourceEntityId <> @SourceEntityId
                      OR CONVERT(varbinary(max), RuleJson) <> CONVERT(varbinary(max), @RuleJson)
                      OR CONVERT(varbinary(max), TimeZoneId) <> CONVERT(varbinary(max), @TimeZoneId)
                      OR ScheduleVersion <> @ScheduleVersion
                      OR IsEnabled <> @IsEnabled
                      OR SourceUpdatedAt IS NULL
                      OR SourceUpdatedAt < @SourceUpdatedAt
                  )
            )
            BEGIN
                UPDATE dbo.ReminderSchedules
                SET ReminderType = @ReminderType,
                    SourceEntity = @SourceEntity,
                    SourceEntityId = @SourceEntityId,
                    RuleJson = @RuleJson,
                    TimeZoneId = @TimeZoneId,
                    ScheduleVersion = @ScheduleVersion,
                    IsEnabled = @IsEnabled,
                    -- The central scheduler owns the execution cursor after initial import.
                    -- Reset it only when the tenant explicitly increments ScheduleVersion.
                    NextRunAtUtc = CASE
                        WHEN ScheduleVersion <> @ScheduleVersion THEN @NextRunAtUtc
                        ELSE NextRunAtUtc
                    END,
                    SourceUpdatedAt = @SourceUpdatedAt,
                    UpdatedAt = SYSUTCDATETIME()
                WHERE TenantDatabaseId = @TenantDatabaseId
                  AND TenantReminderScheduleId = @TenantReminderScheduleId;

                SET @Action = 2; -- Updated
            END
            ELSE
            BEGIN
                SET @Action = 0; -- Unchanged
            END;

            SELECT @Action;
            """, connection, transaction);

        command.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenant.TenantId;
        command.Parameters.Add("@TenantDatabaseId", SqlDbType.BigInt).Value = tenant.TenantDatabaseId;
        command.Parameters.Add("@TenantReminderScheduleId", SqlDbType.BigInt).Value = schedule.ReminderScheduleId;
        command.Parameters.Add("@ReminderType", SqlDbType.NVarChar, 100).Value = schedule.ReminderType;
        command.Parameters.Add("@SourceEntity", SqlDbType.NVarChar, 100).Value = schedule.SourceEntity;
        command.Parameters.Add("@SourceEntityId", SqlDbType.BigInt).Value = schedule.SourceEntityId;
        command.Parameters.Add("@RuleJson", SqlDbType.NVarChar, -1).Value = schedule.RuleJson;
        command.Parameters.Add("@TimeZoneId", SqlDbType.NVarChar, 100).Value = schedule.TimeZoneId;
        command.Parameters.Add("@ScheduleVersion", SqlDbType.Int).Value = schedule.ScheduleVersion;
        command.Parameters.Add("@IsEnabled", SqlDbType.Bit).Value = schedule.IsEnabled;
        command.Parameters.Add("@NextRunAtUtc", SqlDbType.DateTime2).Value =
            (object?)schedule.NextRunAtUtc ?? DBNull.Value;
        command.Parameters.Add("@SourceUpdatedAt", SqlDbType.DateTime2).Value = schedule.UpdatedAt;

        var result = await command.ExecuteScalarAsync(ct);
        return (UpsertAction)Convert.ToByte(result);
    }

    private static async Task UpdateWatermarkAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long tenantDatabaseId,
        DateTime? lastSyncedAtUtc,
        CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.TenantReminderSyncState
            SET LastSyncedAtUtc = CASE
                    WHEN @LastSyncedAtUtc IS NULL THEN LastSyncedAtUtc
                    WHEN LastSyncedAtUtc IS NULL OR LastSyncedAtUtc < @LastSyncedAtUtc
                        THEN @LastSyncedAtUtc
                    ELSE LastSyncedAtUtc
                END,
                LastSyncAtUtc = SYSUTCDATETIME(),
                LastError = NULL
            WHERE TenantDatabaseId = @TenantDatabaseId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.TenantReminderSyncState
                    (TenantDatabaseId, LastSyncedAtUtc, LastSyncAtUtc, LastError)
                VALUES
                    (@TenantDatabaseId, @LastSyncedAtUtc, SYSUTCDATETIME(), NULL);
            END;
            """, connection, transaction);

        command.Parameters.Add("@TenantDatabaseId", SqlDbType.BigInt).Value = tenantDatabaseId;
        command.Parameters.Add("@LastSyncedAtUtc", SqlDbType.DateTime2).Value =
            (object?)lastSyncedAtUtc ?? DBNull.Value;

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task TryRecordFailureAsync(
        long tenantDatabaseId,
        Exception exception,
        CancellationToken ct)
    {
        try
        {
            await using var connection = await centralConnections.OpenAsync(ct);
            await using var command = new SqlCommand("""
                UPDATE dbo.TenantReminderSyncState
                SET LastSyncAtUtc = SYSUTCDATETIME(),
                    LastError = @LastError
                WHERE TenantDatabaseId = @TenantDatabaseId;

                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO dbo.TenantReminderSyncState
                        (TenantDatabaseId, LastSyncAtUtc, LastError)
                    VALUES
                        (@TenantDatabaseId, SYSUTCDATETIME(), @LastError);
                END;
                """, connection);

            var error = $"{exception.GetType().Name}: {exception.Message}";
            if (error.Length > 2000)
                error = error[..2000];

            command.Parameters.Add("@TenantDatabaseId", SqlDbType.BigInt).Value = tenantDatabaseId;
            command.Parameters.Add("@LastError", SqlDbType.NVarChar, 2000).Value = error;
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception stateException)
        {
            logger.LogError(
                stateException,
                "Could not persist Reminder sync failure state for TenantDatabaseId {TenantDatabaseId}.",
                tenantDatabaseId);
        }
    }

    private sealed record TenantReminderSchedule(
        long ReminderScheduleId,
        string ReminderType,
        string SourceEntity,
        long SourceEntityId,
        string RuleJson,
        string TimeZoneId,
        int ScheduleVersion,
        bool IsEnabled,
        DateTime? NextRunAtUtc,
        DateTime UpdatedAt);

    private sealed record SyncResult(
        int Read,
        int Inserted,
        int Updated,
        int Unchanged,
        int StaleSkipped);

    private enum UpsertAction : byte
    {
        Unchanged = 0,
        Inserted = 1,
        Updated = 2,
        StaleSkipped = 3
    }
}
