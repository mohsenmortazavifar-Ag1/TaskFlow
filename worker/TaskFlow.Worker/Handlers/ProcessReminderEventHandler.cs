using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Handlers;

/// <summary>
/// Processes reminder domain events directly against the tenant database.
/// Inbox and schedule changes are committed atomically in the tenant database.
/// </summary>
public sealed class ProcessReminderEventHandler(
    TenantRegistryRepository registry,
    TenantSqlConnectionFactory tenantConnections,
    TenantDatabaseLock tenantLock,
    ILogger<ProcessReminderEventHandler> logger) : IJobHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string JobType => "ProcessReminderEvent";

    public async Task HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!job.TenantId.HasValue || job.TenantId.Value <= 0)
            throw new InvalidOperationException("Reminder event jobs require a positive TenantId.");

        var payload = JsonSerializer.Deserialize<ReminderEventPayload>(job.Payload, JsonOptions)
            ?? throw new InvalidOperationException("Reminder event payload is empty or invalid.");

        if (payload.TenantId != job.TenantId.Value || payload.TenantDatabaseId <= 0 ||
            payload.EventId == Guid.Empty || string.IsNullOrWhiteSpace(payload.EventType) ||
            payload.EventType.Length > 100 || !Regex.IsMatch(payload.EventType, "^[A-Za-z][A-Za-z0-9_.-]*$") ||
            string.IsNullOrWhiteSpace(payload.SourceEntity) || payload.SourceEntity.Length > 100 ||
            !long.TryParse(payload.SourceEntityId, NumberStyles.None, CultureInfo.InvariantCulture, out var sourceEntityId) ||
            sourceEntityId <= 0 || !DateTimeOffset.TryParse(payload.OccurredAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var occurredAt))
            throw new InvalidOperationException("Reminder event payload failed validation.");

        payload.Data ??= new Dictionary<string, string>();
        if (payload.Data.Any(x => x.Key.Length > 100 || x.Value is null || x.Value.Length > 2000))
            throw new InvalidOperationException("Reminder event data contains an invalid key or value.");

        var tenant = await registry.GetActiveDatabaseAsync(payload.TenantId, payload.TenantDatabaseId, cancellationToken)
            ?? throw new InvalidOperationException($"Active tenant database was not found. TenantId={payload.TenantId}, TenantDatabaseId={payload.TenantDatabaseId}.");

        await using var connection = await tenantConnections.OpenAsync(tenant, cancellationToken);
        await using var lease = await tenantLock.AcquireAsync(
            connection, tenant.TenantDatabaseId, $"job:{job.JobId}", cancellationToken);
        var schedules = await ProcessTenantEventAsync(connection, payload, sourceEntityId, occurredAt.UtcDateTime, cancellationToken);
        logger.LogInformation(
            "Processed reminder event {EventId} ({EventType}) for tenant {TenantId}. Created={Created}, affected schedules={Affected}.",
            payload.EventId, payload.EventType, payload.TenantId, schedules.Created, schedules.Affected);
    }

    private static async Task<(int Created, int Affected)> ProcessTenantEventAsync(
        SqlConnection connection, ReminderEventPayload e, long sourceEntityId, DateTime occurredAtUtc, CancellationToken ct)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            await using (var existing = new SqlCommand("""
                SELECT 1 FROM notification.ReminderEventInbox WITH (UPDLOCK, HOLDLOCK)
                WHERE EventId = @EventId;
                """, connection, transaction))
            {
                existing.Parameters.Add("@EventId", SqlDbType.UniqueIdentifier).Value = e.EventId;
                if (await existing.ExecuteScalarAsync(ct) is not null)
                {
                    await transaction.CommitAsync(ct);
                    return (0, 0);
                }
            }

            await using (var insertInbox = new SqlCommand("""
                INSERT INTO notification.ReminderEventInbox
                    (EventId, EventType, SourceEntity, SourceEntityId, OccurredAtUtc, Status)
                VALUES (@EventId, @EventType, @SourceEntity, @SourceEntityId, @OccurredAtUtc, 0);
                """, connection, transaction))
            {
                insertInbox.Parameters.Add("@EventId", SqlDbType.UniqueIdentifier).Value = e.EventId;
                insertInbox.Parameters.Add("@EventType", SqlDbType.NVarChar, 100).Value = e.EventType;
                insertInbox.Parameters.Add("@SourceEntity", SqlDbType.NVarChar, 100).Value = e.SourceEntity;
                insertInbox.Parameters.Add("@SourceEntityId", SqlDbType.BigInt).Value = sourceEntityId;
                insertInbox.Parameters.Add("@OccurredAtUtc", SqlDbType.DateTime2).Value = occurredAtUtc;
                await insertInbox.ExecuteNonQueryAsync(ct);
            }

            var values = new Dictionary<string, string>(e.Data, StringComparer.Ordinal)
            {
                ["eventId"] = e.EventId.ToString(),
                ["eventType"] = e.EventType,
                ["sourceEntity"] = e.SourceEntity,
                ["sourceEntityId"] = e.SourceEntityId,
                ["occurredAtUtc"] = e.OccurredAtUtc
            };

            var affected = 0;
            var created = 0;
            var cancellationRules = await ReadRulesAsync(connection, transaction, e.EventType, e.SourceEntity, true, ct);
            foreach (var rule in cancellationRules)
            {
                await using var cancel = new SqlCommand("""
                    UPDATE notification.ReminderSchedules
                    SET IsEnabled = 0, NextRunAtUtc = NULL,
                        ScheduleVersion = ScheduleVersion + 1,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE SourceEntity = @SourceEntity
                      AND SourceEntityId = @SourceEntityId
                      AND (JSON_VALUE(RuleJson, '$.ruleId') = CONVERT(nvarchar(20), @RuleId)
                           OR (JSON_VALUE(RuleJson, '$.ruleId') IS NULL AND ReminderType = @ReminderType))
                      AND IsEnabled = 1;
                    """, connection, transaction);
                cancel.Parameters.Add("@SourceEntity", SqlDbType.NVarChar, 100).Value = e.SourceEntity;
                cancel.Parameters.Add("@SourceEntityId", SqlDbType.BigInt).Value = sourceEntityId;
                cancel.Parameters.Add("@RuleId", SqlDbType.BigInt).Value = rule.Id;
                cancel.Parameters.Add("@ReminderType", SqlDbType.NVarChar, 100).Value = rule.ReminderType;
                affected += await cancel.ExecuteNonQueryAsync(ct);
            }

            var rules = await ReadRulesAsync(connection, transaction, e.EventType, e.SourceEntity, false, ct);
            foreach (var rule in rules)
            {
                if (rule.RecurrenceIntervalValue.HasValue && !rule.MaxOccurrences.HasValue)
                    throw new InvalidOperationException($"Recurring reminder rule {rule.Id} must define MaxOccurrences (1-1000).");

                if (!string.IsNullOrWhiteSpace(rule.DelayDataKey))
                {
                    if (!values.TryGetValue(rule.DelayDataKey, out var dynamicDelay) || string.IsNullOrWhiteSpace(dynamicDelay))
                        continue;
                    if (!string.Equals(rule.DelayUnit, "month", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unsupported dynamic reminder delay unit.");
                }

                var mobile = Render(rule.MobileTemplate, values).Trim();
                var message = Render(rule.MessageTemplate, values).Trim();
                if (mobile.Length is 0 or > 20 || message.Length is 0 or > 2000)
                    throw new InvalidOperationException($"Reminder rule {rule.Id} rendered an invalid mobile or message.");

                var dueAt = !string.IsNullOrWhiteSpace(rule.DelayDataKey)
                    ? CalculateCalendarMonthDueAt(occurredAtUtc, int.Parse(values[rule.DelayDataKey], CultureInfo.InvariantCulture), rule.TimeZoneId, rule.LocalTime)
                    : CalculateInitialDueAt(occurredAtUtc, rule.DelayMinutes, rule.TimeZoneId, rule.LocalTime);

                var ruleJson = JsonSerializer.Serialize(new
                {
                    ruleId = rule.Id,
                    mobile,
                    message,
                    providerCode = rule.ProviderCode,
                    intervalValue = rule.RecurrenceIntervalValue,
                    intervalUnit = rule.RecurrenceIntervalUnit,
                    localTime = rule.LocalTime,
                    maxOccurrences = rule.MaxOccurrences
                }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

                await using var insertSchedule = new SqlCommand("""
                    INSERT INTO notification.ReminderSchedules
                        (ReminderType, SourceEntity, SourceEntityId, RuleJson, TimeZoneId,
                         ScheduleVersion, IsEnabled, NextRunAtUtc, CreatedAt, UpdatedAt)
                    VALUES
                        (@ReminderType, @SourceEntity, @SourceEntityId, @RuleJson, @TimeZoneId,
                         1, 1, @NextRunAtUtc, SYSUTCDATETIME(), SYSUTCDATETIME());
                    """, connection, transaction);
                insertSchedule.Parameters.Add("@ReminderType", SqlDbType.NVarChar, 100).Value = rule.ReminderType;
                insertSchedule.Parameters.Add("@SourceEntity", SqlDbType.NVarChar, 100).Value = e.SourceEntity;
                insertSchedule.Parameters.Add("@SourceEntityId", SqlDbType.BigInt).Value = sourceEntityId;
                insertSchedule.Parameters.Add("@RuleJson", SqlDbType.NVarChar, -1).Value = ruleJson;
                insertSchedule.Parameters.Add("@TimeZoneId", SqlDbType.NVarChar, 100).Value = rule.TimeZoneId;
                insertSchedule.Parameters.Add("@NextRunAtUtc", SqlDbType.DateTime2).Value = dueAt;
                created += await insertSchedule.ExecuteNonQueryAsync(ct);
            }

            await using (var finishInbox = new SqlCommand("""
                UPDATE notification.ReminderEventInbox
                SET Status = @Status, CreatedReminderCount = @CreatedCount,
                    ProcessedAtUtc = SYSUTCDATETIME()
                WHERE EventId = @EventId;
                """, connection, transaction))
            {
                finishInbox.Parameters.Add("@Status", SqlDbType.TinyInt).Value = created + affected == 0 ? 2 : 1;
                finishInbox.Parameters.Add("@CreatedCount", SqlDbType.Int).Value = created;
                finishInbox.Parameters.Add("@EventId", SqlDbType.UniqueIdentifier).Value = e.EventId;
                await finishInbox.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return (created, affected);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private static async Task<List<EventRule>> ReadRulesAsync(
        SqlConnection connection, SqlTransaction transaction, string eventType, string sourceEntity, bool cancellation, CancellationToken ct)
    {
        var sql = cancellation
            ? """
              SELECT ReminderEventRuleId, ReminderType, DelayMinutes, DelayDataKey, DelayUnit, TimeZoneId,
                     MobileTemplate, MessageTemplate, ProviderCode, RecurrenceIntervalValue,
                     RecurrenceIntervalUnit, LocalTime, MaxOccurrences
              FROM notification.ReminderEventRules WITH (UPDLOCK, HOLDLOCK)
              WHERE EXISTS (SELECT 1 FROM STRING_SPLIT(CancelEventType, N',') c
                            WHERE LTRIM(RTRIM(c.value)) = @EventType)
                AND SourceEntity = @SourceEntity AND IsEnabled = 1
              ORDER BY ReminderEventRuleId;
              """
            : """
              SELECT ReminderEventRuleId, ReminderType, DelayMinutes, DelayDataKey, DelayUnit, TimeZoneId,
                     MobileTemplate, MessageTemplate, ProviderCode, RecurrenceIntervalValue,
                     RecurrenceIntervalUnit, LocalTime, MaxOccurrences
              FROM notification.ReminderEventRules WITH (UPDLOCK, HOLDLOCK)
              WHERE EventType = @EventType AND SourceEntity = @SourceEntity AND IsEnabled = 1
              ORDER BY ReminderEventRuleId;
              """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@EventType", SqlDbType.NVarChar, 100).Value = eventType;
        command.Parameters.Add("@SourceEntity", SqlDbType.NVarChar, 100).Value = sourceEntity;
        var result = new List<EventRule>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new EventRule(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        return result;
    }

    private static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Regex.Replace(template, @"{{\s*([A-Za-z][A-Za-z0-9_]*)\s*}}", match =>
        {
            var key = match.Groups[1].Value;
            return values.TryGetValue(key, out var value)
                ? value
                : throw new InvalidOperationException($"Reminder template requires missing event value '{key}'.");
        });

    private static DateTime CalculateInitialDueAt(DateTime occurredAtUtc, int delayMinutes, string timeZoneId, string? localTime)
    {
        var delayed = occurredAtUtc.AddMinutes(delayMinutes);
        if (localTime is null) return DateTime.SpecifyKind(delayed, DateTimeKind.Utc);
        var zone = FindTimeZone(timeZoneId);
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(delayed, DateTimeKind.Utc), zone).Date;
        var time = ParseLocalTime(localTime);
        var target = DateTime.SpecifyKind(localDate.Add(time), DateTimeKind.Unspecified);
        var resolved = ResolveLocalTime(target, zone);
        // The requested wall-clock time must never move the reminder before its delay.
        if (resolved < delayed)
            resolved = ResolveLocalTime(target.AddDays(1), zone);
        return resolved;
    }

    private static DateTime CalculateCalendarMonthDueAt(DateTime occurredAtUtc, int months, string timeZoneId, string? localTime)
    {
        if (months is not (3 or 6 or 12))
            throw new InvalidOperationException("Dynamic calendar-month reminder delay must be 3, 6 or 12 months.");
        var zone = FindTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc), zone);
        var targetDate = local.Date.AddMonths(months);
        var target = DateTime.SpecifyKind(targetDate.Add(localTime is null ? local.TimeOfDay : ParseLocalTime(localTime)), DateTimeKind.Unspecified);
        return ResolveLocalTime(target, zone);
    }

    private static TimeZoneInfo FindTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException ex) { throw new InvalidOperationException($"Unknown reminder time zone '{id}'.", ex); }
        catch (InvalidTimeZoneException ex) { throw new InvalidOperationException($"Invalid reminder time zone '{id}'.", ex); }
    }

    private static TimeSpan ParseLocalTime(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToTimeSpan()
            : throw new InvalidOperationException($"Invalid reminder local time '{value}'. Expected HH:mm.");

    private static DateTime ResolveLocalTime(DateTime local, TimeZoneInfo zone)
    {
        for (var i = 0; zone.IsInvalidTime(local) && i < 180; i++) local = local.AddMinutes(1);
        if (zone.IsInvalidTime(local)) throw new InvalidOperationException("Could not resolve daylight-saving gap.");
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).UtcDateTime;
    }

    private sealed record EventRule(long Id, string ReminderType, int DelayMinutes, string? DelayDataKey,
        string? DelayUnit, string TimeZoneId, string MobileTemplate, string MessageTemplate, string? ProviderCode,
        int? RecurrenceIntervalValue, string? RecurrenceIntervalUnit, string? LocalTime, int? MaxOccurrences);

    private sealed class ReminderEventPayload
    {
        public long TenantId { get; set; }
        public long TenantDatabaseId { get; set; }
        public Guid EventId { get; set; }
        public string EventType { get; set; } = "";
        public string SourceEntity { get; set; } = "";
        public string SourceEntityId { get; set; } = "";
        public string OccurredAtUtc { get; set; } = "";
        public Dictionary<string, string> Data { get; set; } = new();
    }
}
