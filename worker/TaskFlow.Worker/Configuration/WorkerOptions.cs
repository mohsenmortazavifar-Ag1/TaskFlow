namespace TaskFlow.Worker.Configuration;

public sealed class WorkerOptions
{
    public string WorkerId { get; set; } = "";
    public int PollingIntervalMilliseconds { get; set; } = 2000;
    public int HeartbeatIntervalSeconds { get; set; } = 15;
    public int RecoveryIntervalSeconds { get; set; } = 30;
    public int MaxConcurrency { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 30;
    public int ShutdownTimeoutSeconds { get; set; } = 30;
}

public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = "";
}

public sealed class SmsGatewayOptions
{
    public string BaseUrl { get; set; } = "";
    public string SendPath { get; set; } = "messages";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 20;
}
