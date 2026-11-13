namespace TaskFlow.Worker.Configuration;

public sealed class TaskFlowApiOptions
{
    public string BaseUrl { get; set; } = "";
    public string ReminderEventsPath { get; set; } = "/api/reminder-events";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 20;
}
