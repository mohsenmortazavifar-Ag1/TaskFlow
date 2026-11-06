namespace TaskFlow.Worker.Configuration;

public sealed class ReminderOptions
{
    public bool Enabled { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 100;
    public int MaxConcurrency { get; set; } = 1;
    public bool TenantSyncEnabled { get; set; } = true;
    public int TenantSyncPollingIntervalSeconds { get; set; } = 60;
}
