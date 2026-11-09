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
        int SuccessCount,
        string? R_Message,
        string? R_Error,
        List<ParsGreenMessageResult>? DataList);

    private sealed record ParsGreenMessageResult(string? MessageId, string? Mobile, decimal? Cost);

    public async Task<SmsSendResult> SendAsync(
        SmsMessage message,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new InvalidOperationException("SmsGateway:BaseUrl is not configured.");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new InvalidOperationException("SmsGateway:ApiKey is not configured.");
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

        // Retain the local correlation key for diagnostics; ParsGreen may ignore this header.
        request.Headers.TryAddWithoutValidation("Authorization", $"basic apikey:{config.ApiKey}");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

        if (result.SuccessCount < 1)
            throw new InvalidOperationException($"ParsGreen returned R_Success=true but SuccessCount={result.SuccessCount}.");

        var providerMessageId = result.DataList?.FirstOrDefault()?.MessageId;
        return new SmsSendResult(providerMessageId);
    }

    private static string Trim(string value) => value.Length <= 800 ? value : value[..800];
}
