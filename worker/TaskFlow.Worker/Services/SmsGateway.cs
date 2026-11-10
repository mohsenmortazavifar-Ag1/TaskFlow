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
/// ParsGreen Apiv2 adapter. The API key is a Worker secret; sender number is resolved
/// from the tenant's TenantSmsSettings row. ParsGreen does not document support for
/// the Idempotency-Key header, so provider-side exactly-once delivery is not guaranteed.
/// </summary>
public sealed class HttpSmsGateway(HttpClient http, IOptions<SmsGatewayOptions> options) : ISmsGateway
{
    private sealed record ParsGreenResponse(
        bool R_Success,
        int R_Code,
        ParsGreenData? Data,
        string? R_Message,
        string? R_Error);

    // The SendSms contract returns delivery submission details inside the Data object.
    private sealed record ParsGreenData(
        int SuccessCount,
        int SendStatus,
        string? Mobile,
        string? ReqID);

    public async Task<SmsSendResult> SendAsync(
        SmsMessage message,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var config = options.Value;

        if (!string.Equals(message.ProviderCode, "ParsGreen", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Unsupported SMS provider '{message.ProviderCode ?? "(null)"}' for tenant {message.TenantId}.");

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new InvalidOperationException("SmsGateway:BaseUrl is not configured.");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("SmsGateway:ApiKey is not configured.");
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
            throw new InvalidOperationException("SmsGateway:BaseUrl must be a valid HTTPS URL (HTTP is allowed only for loopback development).");
        if (string.IsNullOrWhiteSpace(message.SenderNumber))
            throw new InvalidOperationException($"No sender number is configured for tenant {message.TenantId}.");
        if (string.IsNullOrWhiteSpace(message.Mobile) || message.Mobile.Length > 20)
            throw new InvalidOperationException("SMS recipient mobile is invalid.");
        if (string.IsNullOrWhiteSpace(message.Message) || message.Message.Length > 1000)
            throw new InvalidOperationException("SMS body is empty or exceeds the ParsGreen 1000-character limit.");

        var path = string.IsNullOrWhiteSpace(config.SendPath)
            ? "Apiv2/Message/SendSms"
            : config.SendPath.TrimStart('/');

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new
            {
                SmsBody = message.Message,
                Mobiles = new[] { message.Mobile },
                SmsNumber = message.SenderNumber
            })
        };

        request.Headers.Accept.ParseAdd("application/json");

        // ParsGreen Apiv2 expects the API key in its documented authorization format.
        request.Headers.TryAddWithoutValidation("Authorization", $"basic apikey:{config.ApiKey}");

        // Correlation only: this header is not a provider-side idempotency guarantee.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ParsGreen returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {Trim(body)}",
                null, response.StatusCode);

        ParsGreenResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<ParsGreenResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"ParsGreen returned invalid JSON: {Trim(body)}", ex);
        }

        if (result is null)
            throw new InvalidOperationException("ParsGreen returned an empty response.");

        if (!result.R_Success)
        {
            var providerError = !string.IsNullOrWhiteSpace(result.R_Error)
                ? result.R_Error
                : result.R_Message;
            throw new InvalidOperationException(
                $"ParsGreen rejected SMS (code {result.R_Code}): {Trim(providerError ?? "Unknown provider error")}");
        }

        if (result.Data is null)
            throw new InvalidOperationException("ParsGreen returned R_Success=true without the expected Data object.");

        if (result.Data.SuccessCount < 1)
            throw new InvalidOperationException(
                $"ParsGreen returned R_Success=true but Data.SuccessCount={result.Data.SuccessCount}.");

        return new SmsSendResult(result.Data.ReqID);
    }

    private static string Trim(string value) => value.Length <= 800 ? value : value[..800];
}
