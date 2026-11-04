using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;
using TaskFlow.Worker.Services;

namespace TaskFlow.Worker.Handlers;

public sealed class SendSmsHandler(JobRepository jobs, ISmsGateway gateway) : IJobHandler
{
    public string JobType => "SendSms";

    public async Task HandleAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(job.Payload);
        if (!payload.RootElement.TryGetProperty("smsMessageId", out var idElement) ||
            !idElement.TryGetInt64(out var smsMessageId) || smsMessageId <= 0)
            throw new InvalidOperationException("SendSms payload must contain a positive smsMessageId.");

        await using var connection = await jobs.OpenConnectionAsync(cancellationToken);
        await using var read = new SqlCommand("""
            SELECT m.SmsMessageId, m.TenantId, m.Mobile, m.Message, s.ProviderCode, s.SenderNumber, s.IsEnabled
            FROM dbo.SmsMessages m
            LEFT JOIN dbo.TenantSmsSettings s ON s.TenantId = m.TenantId
            WHERE m.SmsMessageId = @SmsMessageId AND m.TenantId = @TenantId;
            """, connection);
        read.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = smsMessageId;
        read.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = job.TenantId ?? throw new InvalidOperationException("SMS jobs require TenantId.");

        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"SMS message {smsMessageId} was not found for this tenant.");

        var sms = new SmsMessage(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
        var enabled = !reader.IsDBNull(6) && reader.GetBoolean(6);
        await reader.CloseAsync();

        if (!enabled) throw new InvalidOperationException($"SMS is disabled or not configured for tenant {sms.TenantId}.");

        var result = await gateway.SendAsync(sms, job.IdempotencyKey ?? $"taskflow-sms-{sms.SmsMessageId}", cancellationToken);
        await using var update = new SqlCommand("""
            UPDATE dbo.SmsMessages
            SET Status = 1, ProviderMessageId = @ProviderMessageId, AcceptedAt = SYSUTCDATETIME(), LastError = NULL
            WHERE SmsMessageId = @SmsMessageId AND TenantId = @TenantId;

            INSERT INTO dbo.SmsLogs(SmsMessageId, ProviderCode, ProviderMessageId, EventType, Description)
            VALUES(@SmsMessageId, @ProviderCode, @ProviderMessageId, N'Accepted', @Description);
            """, connection);
        update.Parameters.Add("@SmsMessageId", SqlDbType.BigInt).Value = sms.SmsMessageId;
        update.Parameters.Add("@TenantId", SqlDbType.BigInt).Value = sms.TenantId;
        update.Parameters.Add("@ProviderCode", SqlDbType.NVarChar, 50).Value = (object?)sms.ProviderCode ?? DBNull.Value;
        update.Parameters.Add("@ProviderMessageId", SqlDbType.NVarChar, 150).Value = (object?)result.ProviderMessageId ?? DBNull.Value;
        update.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value = "Accepted by configured SMS gateway.";
        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}
