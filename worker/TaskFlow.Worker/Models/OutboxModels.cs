namespace TaskFlow.Worker.Models;

public sealed record TenantDatabase(
    long TenantDatabaseId, long TenantId, string ServerName, string DatabaseName,
    string SqlLoginName, string SqlPasswordEncrypted, string? SchemaVersion);

public sealed record OutboxMessage(
    long OutboxId, Guid EventId, string EventType, string QueueName, string JobType,
    string Payload, byte Priority, int AttemptCount, int MaxAttempts);
