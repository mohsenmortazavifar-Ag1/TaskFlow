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
/// Materializes due reminder schedules into durable occurrences and queue jobs.
/// Schedule advancement and job creation happen in one SQL transaction.
/// </summary>
public sealed class ReminderSchedulerService(
    JobRepository jobs,
    IOptions<ReminderOptions> options,
    ILogger<ReminderSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.Enabled)
        {
            logger.LogInformation("Reminder Scheduler is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(config.PollingIntervalSeconds, 2, 300));
        using var timer = new PeriodicTimer(interval);
        logger.LogInformation("Reminder Scheduler started.");

        // Run once immediately; PeriodicTimer's first tick otherwise waits one full interval.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await ProcessDueBatchAsync(
                    Math.Clamp(config.BatchSize, 1, 1000), stoppingToken);
                if (count > 0)
                    logger.LogInformation("Reminder Scheduler materialized {Count} due occurrence(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reminder Scheduler cycle failed.");
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

    private async Task<int> ProcessDueBatchAsync(int batchSize, CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        try
        {
            var due = new List<DueReminderSchedule>();
            await using (var select = new SqlCommand("""
                SELECT TOP (@BatchSize)
                    ReminderScheduleId, TenantId, ReminderType, SourceEntity, SourceEntityId,
                    RuleJson, TimeZoneId, ScheduleVersion, NextRunAtUtc
                FROM dbo.ReminderSchedules WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE IsEnabled = 1
                  AND NextRunAtUtc IS NOT NULL
                  AND NextRunAtUtc <= SYSUTCDATETIME()
                ORDER BY NextRunAtUtc, ReminderScheduleId;
                """, connection, transaction))
            {
                select.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    due.Add(new DueReminderSchedule(
                        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                        reader.GetString(3), reader.GetInt64(4), reader.GetString(5),
                        reader.GetString(6), reader.GetInt32(7), reader.GetDateTime(8)));
            }

            foreach (var schedule in due)
            {
                ct.ThrowIfCancellationRequested();
                var rule = ParseRule(schedule.RuleJson);
                var occurrenceId = await InsertOccurrenceAsync(connection, transaction, schedule, ct);
                var idempotencyKey = $"reminder:{schedule.ReminderScheduleId}:v{schedule.ScheduleVersion}:{schedule.NextRunAtUtc.Ticks}";
                var payload = JsonSerializer.Serialize(new { reminderOccurrenceId = occurrenceId });

                await using (var insertJob = new SqlCommand("""
                    INSERT INTO dbo.Jobs
                        (TenantId, QueueName, JobType, Payload, Priority, ScheduledAt, IdempotencyKey)
                    OUTPUT INSERTED.JobId
                    VALUES
                        (@TenantId, N'reminders', N'ExecuteReminder', @Payload, 10,
                         SYSUTCDATETIME(), @IdempotencyKey);
                    """, connection, transaction))
                {
                    insertJob.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = schedule.TenantId;
                    insertJob.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payload;
                    insertJob.Parameters.Add("@IdempotencyKey", SqlDbType.NVarChar, 200).Value = idempotencyKey;
                    var jobId = Convert.ToInt64(await insertJob.ExecuteScalarAsync(ct));

                    await using var link = new SqlCommand("""
                        UPDATE dbo.ReminderOccurrences
                        SET JobId = @JobId, Status = 1
                        WHERE ReminderOccurrenceId = @OccurrenceId AND Status = 0;
                        """, connection, transaction);
                    link.Parameters.Add("@JobId", SqlDbType.BigInt).Value = jobId;
                    link.Parameters.Add("@OccurrenceId", SqlDbType.BigInt).Value = occurrenceId;
                    await link.ExecuteNonQueryAsync(ct);
                }

                var nextRun = CalculateNextRun(schedule, rule);
                await using var advance = new SqlCommand("""
                    UPDATE dbo.ReminderSchedules
                    SET NextRunAtUtc = @NextRunAtUtc,
                        IsEnabled = CASE WHEN @NextRunAtUtc IS NULL THEN 0 ELSE IsEnabled END,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE ReminderScheduleId = @ScheduleId
                      AND ScheduleVersion = @ScheduleVersion
                      AND IsEnabled = 1;
                    """, connection, transaction);
                advance.Parameters.Add("@NextRunAtUtc", SqlDbType.DateTime2).Value =
                    (object?)nextRun ?? DBNull.Value;
                advance.Parameters.Add("@ScheduleId", SqlDbType.BigInt).Value = schedule.ReminderScheduleId;
                advance.Parameters.Add("@ScheduleVersion", SqlDbType.Int).Value = schedule.ScheduleVersion;
                await advance.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return due.Count;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private static async Task<long> InsertOccurrenceAsync(
        SqlConnection connection, SqlTransaction transaction,
        DueReminderSchedule schedule, CancellationToken ct)
    {
        await using var command = new SqlCommand("""
            INSERT INTO dbo.ReminderOccurrences
                (ReminderScheduleId, ScheduleVersion, DueAtUtc, Status)
            OUTPUT INSERTED.ReminderOccurrenceId
            VALUES (@ScheduleId, @ScheduleVersion, @DueAtUtc, 0);
            """, connection, transaction);
        command.Parameters.Add("@ScheduleId", SqlDbType.BigInt).Value = schedule.ReminderScheduleId;
        command.Parameters.Add("@ScheduleVersion", SqlDbType.Int).Value = schedule.ScheduleVersion;
        command.Parameters.Add("@DueAtUtc", SqlDbType.DateTime2).Value = schedule.NextRunAtUtc;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
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

    private static DateTime? CalculateNextRun(DueReminderSchedule schedule, ReminderRule rule)
    {
        if (!rule.IntervalValue.HasValue)
            return null; // One-shot reminder.

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(schedule.TimeZoneId, out var zone))
            throw new InvalidOperationException($"Unknown reminder time zone '{schedule.TimeZoneId}'.");

        var localDue = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(schedule.NextRunAtUtc, DateTimeKind.Utc), zone);
        var nextLocal = rule.IntervalUnit switch
        {
            "day" => localDue.AddDays(rule.IntervalValue.Value),
            "month" => localDue.AddMonths(rule.IntervalValue.Value),
            "year" => localDue.AddYears(rule.IntervalValue.Value),
            _ => throw new InvalidOperationException("Unsupported reminder recurrence unit.")
        };

        if (rule.LocalTime is not null &&
            TimeOnly.TryParseExact(rule.LocalTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            nextLocal = DateTime.SpecifyKind(nextLocal.Date.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);

        nextLocal = DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified);
        // Move forward through a DST gap to the first valid local minute.
        for (var i = 0; zone.IsInvalidTime(nextLocal) && i < 180; i++)
            nextLocal = nextLocal.AddMinutes(1);
        if (zone.IsInvalidTime(nextLocal))
            throw new InvalidOperationException($"Could not resolve daylight-saving gap for '{schedule.TimeZoneId}'.");

        // During an overlap, choose the larger offset (the earlier UTC instant) deterministically.
        var offset = zone.IsAmbiguousTime(nextLocal)
            ? zone.GetAmbiguousTimeOffsets(nextLocal).Max()
            : zone.GetUtcOffset(nextLocal);
        return new DateTimeOffset(nextLocal, offset).UtcDateTime;
    }
}
