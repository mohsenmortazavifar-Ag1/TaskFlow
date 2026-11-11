using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Handlers;
using TaskFlow.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);
var shutdownTimeoutSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("Worker:ShutdownTimeoutSeconds") ?? 30, 5, 300);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(shutdownTimeoutSeconds));

if (OperatingSystem.IsWindows())
    builder.Services.AddWindowsService();
else if (OperatingSystem.IsLinux())
    builder.Services.AddSystemd();

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<SmsGatewayOptions>(builder.Configuration.GetSection("SmsGateway"));
builder.Services.Configure<SmsDeliveryOptions>(builder.Configuration.GetSection("SmsDelivery"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection("Reminder"));
builder.Services.Configure<TenantRegistryOptions>(builder.Configuration.GetSection("TenantRegistry"));
builder.Services.Configure<TenantPasswordEncryptionOptions>(builder.Configuration.GetSection("TenantPasswordEncryption"));
builder.Services.Configure<TaskFlowApiOptions>(builder.Configuration.GetSection("TaskFlowApi"));

builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton<JobRepository>();
builder.Services.AddSingleton<IJobHandler, SendSmsHandler>();
builder.Services.AddSingleton<IJobHandler, ExecuteReminderHandler>();
builder.Services.AddSingleton<IJobHandler, ProcessReminderEventHandler>();
builder.Services.AddSingleton<JobHandlerRegistry>();
builder.Services.AddHttpClient<ISmsGateway, HttpSmsGateway>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SmsGatewayOptions>>().Value;
    if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        client.BaseAddress = baseUri;
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120));
});
builder.Services.AddHttpClient("TaskFlowReminderEvents", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<TaskFlowApiOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120));
});

builder.Services.AddSingleton<WorkerHeartbeatService>();
builder.Services.AddHostedService<WorkerHeartbeatService>(sp => sp.GetRequiredService<WorkerHeartbeatService>());
builder.Services.AddHostedService<QueuePollingWorker>();
builder.Services.AddHostedService<ExpiredJobRecoveryService>();
builder.Services.AddHostedService<SmsDeliveryPollingService>();

// Tenant Outbox Dispatcher, Reminder Scheduler and queue consumers share this Worker process.
builder.Services.AddSingleton<ITenantSecretDecryptor, TenantSecretDecryptor>();
builder.Services.AddSingleton<TenantRegistryRepository>();
builder.Services.AddSingleton<TenantSqlConnectionFactory>();
builder.Services.AddSingleton<TenantOutboxRepository>();
builder.Services.AddSingleton<CentralJobPublisher>();
builder.Services.AddHostedService<OutboxDispatcherService>();
builder.Services.AddHostedService<TenantReminderSyncService>();
builder.Services.AddHostedService<ReminderSchedulerService>();

var host = builder.Build();
await host.RunAsync();
