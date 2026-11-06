namespace TaskFlow.Worker.Models;

public sealed record DueReminderSchedule(
    long ReminderScheduleId,
    long TenantId,
    string ReminderType,
    string SourceEntity,
    long SourceEntityId,
    string RuleJson,
    string TimeZoneId,
    int ScheduleVersion,
    DateTime NextRunAtUtc);

public sealed record ReminderRule(
    string Mobile,
    string Message,
    string? ProviderCode,
    int? IntervalValue,
    string? IntervalUnit,
    string? LocalTime);
