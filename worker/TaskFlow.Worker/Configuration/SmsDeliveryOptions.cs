namespace TaskFlow.Worker.Configuration;

public sealed class SmsDeliveryOptions
{
    public bool Enabled { get; set; } = true;
    public int PollingIntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 20;
    public int FirstCheckDelaySeconds { get; set; } = 120;
    public int SubsequentCheckDelaySeconds { get; set; } = 120;
    public int SendingTimeoutMinutes { get; set; } = 5;
}
