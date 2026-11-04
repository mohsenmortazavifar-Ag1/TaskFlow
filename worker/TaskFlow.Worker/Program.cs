using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Handlers;
using TaskFlow.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<SmsGatewayOptions>(builder.Configuration.GetSection("SmsGateway"));

builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton<JobRepository>();
builder.Services.AddSingleton<IJobHandler, SendSmsHandler>();
builder.Services.AddSingleton<JobHandlerRegistry>();
builder.Services.AddHttpClient<ISmsGateway, HttpSmsGateway>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SmsGatewayOptions>>().Value;
    if (Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        client.BaseAddress = baseUri;
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 120));
    if (!string.IsNullOrWhiteSpace(options.ApiKey))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
});

builder.Services.AddSingleton<WorkerHeartbeatService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerHeartbeatService>());
builder.Services.AddHostedService<QueuePollingWorker>();
builder.Services.AddHostedService<ExpiredJobRecoveryService>();

var host = builder.Build();
await host.RunAsync();
