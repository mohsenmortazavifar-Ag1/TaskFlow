namespace TaskFlow.Worker.Models;

public sealed record QueueDefinition(
    string QueueName,
    bool IsEnabled,
    int MaxConcurrency,
    int VisibilityTimeoutSeconds,
    int PollingIntervalSeconds);

public sealed record ClaimedJob(
    long JobId,
    long? TenantId,
    string QueueName,
    string JobType,
    string Payload,
    byte AttemptCount,
    byte MaxAttempts,
    string? IdempotencyKey);

public sealed record SmsMessage(
    long SmsMessageId,
    long TenantId,
    string Mobile,
    string Message,
    string? ProviderCode,
    string? SenderNumber);
