using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Services;

public sealed record SmsSendResult(string? ProviderMessageId);

public interface ISmsGateway
{
    Task<SmsSendResult> SendAsync(SmsMessage message, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>
/// Generic HTTP adapter. The configured endpoint must implement the contract documented in worker/README.md.
/// Replace this adapter when integrating a provider-specific API.
/// </summary>
public sealed class HttpSmsGateway(HttpClient http, IOptions<SmsGatewayOptions> options) : ISmsGateway
{
    public async Task<SmsSendResult> SendAsync(SmsMessage message, string idempotencyKey, CancellationToken cancellationToken)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new InvalidOperationException("SmsGateway:BaseUrl is not configured.");

        using var request = new HttpRequestMessage(HttpMethod.Post, config.SendPath)
        {
            Content = JsonContent.Create(new
            {
                mobile = message.Mobile,
                message = message.Message,
                senderNumber = message.SenderNumber,
                tenantId = message.TenantId
            })
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SMS gateway returned {(int)response.StatusCode}: {Trim(body)}");

        using var json = JsonDocument.Parse(body);
        var accepted = json.RootElement.TryGetProperty("accepted", out var acceptedElement) &&
                       acceptedElement.ValueKind == JsonValueKind.True;
        if (!accepted)
            throw new InvalidOperationException($"SMS gateway did not confirm acceptance: {Trim(body)}");

        var providerId = json.RootElement.TryGetProperty("providerMessageId", out var providerElement)
            ? providerElement.GetString()
            : null;
        return new SmsSendResult(providerId);
    }

    private static string Trim(string value) => value.Length <= 800 ? value : value[..800];
}
