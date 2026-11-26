using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Services;

/// <summary>
/// Claims due schedules directly from each tenant database. Occurrence creation,
/// schedule advancement and the execution Outbox event are one tenant-local transaction.
/// </summary>
public sealed class TenantReminderSchedulerService(
    TenantRegistryRepository registry,
    TenantSqlConnectionFactory tenantConnections,
    TenantDatabaseLock tenantLock,
    WorkerHeartbeatService heartbeat,
    IOptions<ReminderOptions> options,
    ILogger<TenantReminderSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.Enabled)
        {
            logger.LogInformation("Tenant-owned Reminder Scheduler is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(config.PollingIntervalSeconds, 2, 300)));
        logger.LogInformation("Tenant-owned Reminder Scheduler started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tenants = await registry.GetActiveDatabasesAsync(stoppingToken);
                await Parallel.ForEachAsync(tenants, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(config.MaxConcurrency, 1, 16),
                    CancellationToken = stoppingToken
                }, async (tenant, ct) => await ProcessTenantAsync(tenant, config.BatchSize, ct));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Tenant-owned Reminder Scheduler cycle failed."); }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessTenantAsync(TenantDatabase tenant, int batchSize, CancellationToken ct)
    {
        try
        {
            await using var connection = await tenantConnections.OpenAsync(tenant, ct);
            await using var lease = await tenantLock.AcquireAsync(
                connection, tenant.TenantDatabaseId, heartbeat.WorkerId, ct);

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                var due = new List<Schedule>();
                await using (var read = new SqlCommand("""
                    SELECT TOP (@BatchSize)
                        ReminderScheduleId, ReminderType, SourceEntity, SourceEntityId,
                        RuleJson, TimeZoneId, ScheduleVersion, NextRunAtUtc, OccurrenceCount
                    FROM notification.ReminderSchedules WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE IsEnabled = 1 AND NextRunAtUtc IS NOT NULL
                      AND NextRunAtUtc <= SYSUTCDATETIME()
                    ORDER BY NextRunAtUtc, ReminderScheduleId;
                    """, connection, transaction))
                {
                    read.Parameters.Add("@BatchSize", SqlDbType.Int).Value = Math.Clamp(batchSize, 1, 1000);
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        due.Add(new Schedule(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                            reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                            reader.GetDateTime(7), reader.GetInt32(8)));
                }

                foreach (var schedule in due)
                {
                    ct.ThrowIfCancellationRequested();
                    var rule = ParseRule(schedule.RuleJson);
                    var eventId = Guid.NewGuid();
                    var nextRun = rule.MaxOccurrences.HasValue && schedule.OccurrenceCount + 1 >= rule.MaxOccurrences.Value
                        ? null
                        : CalculateNextRun(schedule.NextRunAtUtc, schedule.TimeZoneId, rule);

                    await using (var occurrence = new SqlCommand("""
                        INSERT notification.ReminderOccurrences
                            (ReminderScheduleId, ScheduleVersion, DueAtUtc, Status, ExecutionEventId)
                        OUTPUT INSERTED.ReminderOccurrenceId
                        VALUES (@ScheduleId, @Version, @DueAtUtc, 1, @EventId);
                        """, connection, transaction))
                    {
                        occurrence.Parameters.Add("@ScheduleId", SqlDbType.BigInt).Value = schedule.Id;
                        occurrence.Parameters.Add("@Version", SqlDbType.Int).Value = schedule.Version;
                        occurrence.Parameters.Add("@DueAtUtc", SqlDbType.DateTime2).Value = schedule.NextRunAtUtc;
                        occurrence.Parameters.Add("@EventId", SqlDbType.UniqueIdentifier).Value = eventId;
                        var occurrenceId = Convert.ToInt64(await occurrence.ExecuteScalarAsync(ct));

                        var payload = JsonSerializer.Serialize(new
                        {
                            tenantId = tenant.TenantId,
                            tenantDatabaseId = tenant.TenantDatabaseId,
                            reminderOccurrenceId = occurrenceId
                        });

                        await using var outbox = new SqlCommand("""
                            INSERT notification.OutboxMessages
                                (EventId, TenantId, EventType, QueueName, JobType, Payload, Priority)
                            VALUES
                                (@EventId, @TenantId, N'ReminderOccurrenceDue', N'reminders',
                                 N'ExecuteReminder', @Payload, 10);
                            """, connection, transaction);
                        outbox.Parameters.Add("@EventId", SqlDbType.UniqueIdentifier).Value = eventId;
                        outbox.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = tenant.TenantId;
                        outbox.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payload;
                        await outbox.ExecuteNonQueryAsync(ct);
                    }

                    await using var advance = new SqlCommand("""
                        UPDATE notification.ReminderSchedules
                        SET NextRunAtUtc = @NextRunAtUtc,
                            OccurrenceCount = OccurrenceCount + 1,
                            UpdatedAt = SYSUTCDATETIME()
                        WHERE ReminderScheduleId = @ScheduleId
                          AND ScheduleVersion = @Version
                          AND IsEnabled = 1;
                        """, connection, transaction);
                    advance.Parameters.Add("@NextRunAtUtc", SqlDbType.DateTime2).Value = (object?)nextRun ?? DBNull.Value;
                    advance.Parameters.Add("@ScheduleId", SqlDbType.BigInt).Value = schedule.Id;
                    advance.Parameters.Add("@Version", SqlDbType.Int).Value = schedule.Version;
                    if (await advance.ExecuteNonQueryAsync(ct) != 1)
                        throw new InvalidOperationException($"Reminder schedule {schedule.Id} changed while being materialized.");
                }

                await transaction.CommitAsync(ct);
                if (due.Count > 0)
                    logger.LogInformation("Created {Count} tenant reminder occurrence(s). TenantDatabaseId={TenantDatabaseId}.",
                        due.Count, tenant.TenantDatabaseId);
            }
            catch
            {
                try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Reminder scheduling failed for TenantDatabaseId {TenantDatabaseId}.", tenant.TenantDatabaseId);
        }
    }

    private static Rule ParseRule(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int? interval = root.TryGetProperty("intervalValue", out var iv) && iv.TryGetInt32(out var i) ? i : null;
        var unit = root.TryGetProperty("intervalUnit", out var iu) ? iu.GetString() : null;
        int? max = root.TryGetProperty("maxOccurrences", out var mo) && mo.TryGetInt32(out var m) ? m : null;
        var localTime = root.TryGetProperty("localTime", out var lt) ? lt.GetString() : null;
        if (interval.HasValue && (interval.Value < 1 || interval.Value > 3650 || unit is not ("day" or "month" or "year")))
            throw new InvalidOperationException("Reminder recurrence interval is invalid.");
        if (interval.HasValue && !max.HasValue)
            throw new InvalidOperationException("Recurring reminders must have a finite MaxOccurrences value.");
        if (!interval.HasValue && unit is not null)
            throw new InvalidOperationException("Reminder recurrence unit cannot exist without an interval.");
        if (max.HasValue && max.Value is < 1 or > 1000)
            throw new InvalidOperationException("Reminder MaxOccurrences must be between 1 and 1000.");
        if (localTime is not null && !TimeOnly.TryParseExact(localTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Reminder LocalTime must use HH:mm.");
        return new Rule(interval, unit, max, localTime);
    }

    private static DateTime? CalculateNextRun(DateTime dueUtc, string timeZoneId, Rule rule)
    {
        if (!rule.IntervalValue.HasValue) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(dueUtc, DateTimeKind.Utc), zone);
        var next = rule.IntervalUnit switch
        {
            "day" => local.AddDays(rule.IntervalValue.Value),
            "month" => local.AddMonths(rule.IntervalValue.Value),
            "year" => local.AddYears(rule.IntervalValue.Value),
            _ => throw new InvalidOperationException("Unsupported reminder recurrence unit.")
        };
        if (rule.LocalTime is not null)
            next = DateTime.SpecifyKind(next.Date.Add(TimeOnly.ParseExact(rule.LocalTime, "HH:mm", CultureInfo.InvariantCulture).ToTimeSpan()), DateTimeKind.Unspecified);
        else
            next = DateTime.SpecifyKind(next, DateTimeKind.Unspecified);

        for (var n = 0; zone.IsInvalidTime(next) && n < 180; n++) next = next.AddMinutes(1);
        if (zone.IsInvalidTime(next)) throw new InvalidOperationException("Could not resolve daylight-saving gap.");
        var offset = zone.IsAmbiguousTime(next) ? zone.GetAmbiguousTimeOffsets(next).Max() : zone.GetUtcOffset(next);
        return new DateTimeOffset(next, offset).UtcDateTime;
    }

    private sealed record Schedule(long Id, string ReminderType, string SourceEntity, long SourceEntityId,
        string RuleJson, string TimeZoneId, int Version, DateTime NextRunAtUtc, int OccurrenceCount);
    private sealed record Rule(int? IntervalValue, string? IntervalUnit, int? MaxOccurrences, string? LocalTime);
}
