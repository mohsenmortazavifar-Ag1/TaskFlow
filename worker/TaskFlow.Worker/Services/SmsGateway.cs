using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Services;

public sealed record SmsSendResult(string? ProviderMessageId);

public sealed record SmsDeliveryResult(
    bool IsSuccess,
    int DeliveryStatus,
    int ResponseCode,
    string? Message,
    string? Error);

public sealed class SmsGatewayConfigurationException(string message) : InvalidOperationException(message);

public sealed class SmsProviderRejectedException(string message) : Exception(message);

public sealed class SmsSubmissionOutcomeUnknownException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public interface ISmsGateway
{
    void Validate(SmsMessage message);

    Task<SmsSendResult> SendAsync(
        SmsMessage message,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<SmsDeliveryResult> CheckDeliveryAsync(
        string providerMessageId,
        CancellationToken cancellationToken);
}

/// <summary>
/// ParsGreen Apiv2 adapter. ParsGreen does not document provider-side idempotency.
/// Submission ambiguity is surfaced explicitly so the caller never blindly resends.
/// </summary>
public sealed class HttpSmsGateway(
    HttpClient http,
    IOptions<SmsGatewayOptions> options) : ISmsGateway
{
    private sealed record ParsGreenSendResponse(
        bool R_Success,
        int R_Code,
        int SuccessCount,
        List<ParsGreenSendItem>? DataList,
        string? R_Message,
        string? R_Error);

    private sealed record ParsGreenSendItem(
        int SendStatus,
        string? Mobile,
        string? ReqID);

    private sealed record ParsGreenDeliveryResponse(
        int DeliveryStatus,
        bool R_Success,
        int R_Code,
        string? R_Error,
        string? R_Message);

    public void Validate(SmsMessage message)
    {
        var config = options.Value;

        if (!string.Equals(message.ProviderCode, "ParsGreen", StringComparison.OrdinalIgnoreCase))
            throw new SmsGatewayConfigurationException(
                $"Unsupported SMS provider '{message.ProviderCode ?? "(null)"}' for tenant {message.TenantId}.");

        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new SmsGatewayConfigurationException("SmsGateway:BaseUrl is not configured.");
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
            throw new SmsGatewayConfigurationException(
                "SmsGateway:BaseUrl must be a valid HTTPS URL (HTTP is allowed only for loopback development).");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new SmsGatewayConfigurationException("SmsGateway:ApiKey is not configured.");
        if (string.IsNullOrWhiteSpace(message.SenderNumber))
            throw new SmsGatewayConfigurationException(
                $"No sender number is configured for tenant {message.TenantId}.");
        if (string.IsNullOrWhiteSpace(message.Mobile) || message.Mobile.Length > 20)
            throw new SmsGatewayConfigurationException("SMS recipient mobile is invalid.");
        if (string.IsNullOrWhiteSpace(message.Message) || message.Message.Length > 1000)
            throw new SmsGatewayConfigurationException(
                "SMS body is empty or exceeds the ParsGreen 1000-character limit.");
    }

    public async Task<SmsSendResult> SendAsync(
        SmsMessage message,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Validate(message);
        var config = options.Value;
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

        AddProviderHeaders(request, config.ApiKey);
        // Correlation only. ParsGreen does not document this as an idempotency contract.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        string body;
        try
        {
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new SmsSubmissionOutcomeUnknownException(
                    $"ParsGreen returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}); " +
                    $"provider acceptance is unknown. Response: {Trim(body)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown/cancellation after the request started is ambiguous. Leave the durable
            // Sending state in place; it will be moved to NeedsReview, never resent.
            throw;
        }
        catch (SmsSubmissionOutcomeUnknownException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SmsSubmissionOutcomeUnknownException(
                "ParsGreen SendSms request failed after dispatch; provider acceptance is unknown.", ex);
        }

        ParsGreenSendResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<ParsGreenSendResponse>(
                body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new SmsSubmissionOutcomeUnknownException(
                $"ParsGreen returned an unreadable SendSms response; provider acceptance is unknown. Response: {Trim(body)}", ex);
        }

        if (result is null)
            throw new SmsSubmissionOutcomeUnknownException(
                "ParsGreen returned an empty SendSms response; provider acceptance is unknown.");

        if (!result.R_Success)
        {
            var providerError = !string.IsNullOrWhiteSpace(result.R_Error)
                ? result.R_Error
                : result.R_Message;
            throw new SmsProviderRejectedException(
                $"ParsGreen rejected SMS (code {result.R_Code}): {Trim(providerError ?? "Unknown provider error")}");
        }

        if (result.SuccessCount < 1)
            throw new SmsProviderRejectedException(
                $"ParsGreen returned R_Success=true but SuccessCount={result.SuccessCount}. " +
                $"Message: {Trim(result.R_Message ?? "No provider message")}");

        var providerMessageId = result.DataList?
            .FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.ReqID) &&
                string.Equals(x.Mobile?.Trim(), message.Mobile.Trim(), StringComparison.OrdinalIgnoreCase))
            ?.ReqID;

        providerMessageId ??= result.DataList?
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ReqID))
            ?.ReqID;

        // SuccessCount is provider acceptance, not delivery. ReqID is required for CheckDelivery.
        return new SmsSendResult(providerMessageId);
    }

    public async Task<SmsDeliveryResult> CheckDeliveryAsync(
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        var config = options.Value;
        if (string.IsNullOrWhiteSpace(config.BaseUrl) || string.IsNullOrWhiteSpace(config.ApiKey))
            throw new SmsGatewayConfigurationException(
                "SmsGateway:BaseUrl and SmsGateway:ApiKey are required for CheckDelivery.");
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback))
            throw new SmsGatewayConfigurationException(
                "SmsGateway:BaseUrl must be a valid HTTPS URL (HTTP is allowed only for loopback development).");
        if (string.IsNullOrWhiteSpace(providerMessageId))
            throw new ArgumentException("Provider message ID is required.", nameof(providerMessageId));

        var path = string.IsNullOrWhiteSpace(config.CheckDeliveryPath)
            ? "Apiv2/Message/CheckDelivery"
            : config.CheckDeliveryPath.TrimStart('/');

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { RecId = providerMessageId })
        };
        AddProviderHeaders(request, config.ApiKey);

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ParsGreen CheckDelivery returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {Trim(body)}",
                null, response.StatusCode);

        ParsGreenDeliveryResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<ParsGreenDeliveryResponse>(
                body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"ParsGreen returned invalid CheckDelivery JSON: {Trim(body)}", ex);
        }

        if (result is null)
            throw new InvalidOperationException("ParsGreen returned an empty CheckDelivery response.");

        return new SmsDeliveryResult(
            result.R_Success,
            result.DeliveryStatus,
            result.R_Code,
            result.R_Message,
            result.R_Error);
    }

    private static void AddProviderHeaders(HttpRequestMessage request, string apiKey)
    {
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation("Authorization", $"basic apikey:{apiKey}");
    }

    private static string Trim(string value) => value.Length <= 800 ? value : value[..800];
}
