using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Handlers;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Services;

/// <summary>Delivers persisted tenant domain events to TaskFlow's authenticated event API.</summary>
public sealed class ProcessReminderEventHandler(
    IHttpClientFactory clients,
    IOptions<TaskFlowApiOptions> options,
    ILogger<ProcessReminderEventHandler> logger) : IJobHandler
{
    public string JobType => "ProcessReminderEvent";

    public async Task HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        if (!job.TenantId.HasValue || job.TenantId.Value <= 0)
            throw new InvalidOperationException("Reminder event jobs require a positive TenantId.");

        using var document = JsonDocument.Parse(job.Payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("tenantId", out var tenantNode) ||
            !tenantNode.TryGetInt64(out var payloadTenantId) ||
            payloadTenantId != job.TenantId.Value)
            throw new InvalidOperationException("Reminder event payload TenantId does not match the queued job.");

        if (!root.TryGetProperty("eventId", out var eventNode) ||
            !Guid.TryParse(eventNode.GetString(), out _) ||
            !root.TryGetProperty("eventType", out var typeNode) ||
            !string.Equals(typeNode.GetString(), "RepairCompleted", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported or invalid reminder event payload.");

        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("TaskFlowApi:BaseUrl and TaskFlowApi:ApiKey are required.");

        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !string.Equals(baseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("TaskFlowApi:BaseUrl must be an absolute HTTPS URL (localhost is allowed for development).");

        var path = string.IsNullOrWhiteSpace(config.ReminderEventsPath)
            ? "/api/reminder-events"
            : config.ReminderEventsPath;
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidOperationException("TaskFlowApi:ReminderEventsPath must be an absolute path on the configured host.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path))
        {
            Content = JsonContent.Create(root)
        };
        request.Headers.TryAddWithoutValidation("x-taskflow-api-key", config.ApiKey);

        using var response = await clients.CreateClient("TaskFlowReminderEvents")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 1000) body = body[..1000];
            throw new HttpRequestException(
                $"TaskFlow Reminder Events API returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body}",
                null, response.StatusCode);
        }

        logger.LogInformation("Accepted reminder event {EventId} for tenant {TenantId}.",
            eventNode.GetString(), job.TenantId.Value);
    }
}
