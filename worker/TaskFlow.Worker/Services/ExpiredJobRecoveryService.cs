using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;

namespace TaskFlow.Worker.Services;

/// <summary>Periodically releases jobs whose worker lease expired.</summary>
public sealed class ExpiredJobRecoveryService(
    SqlConnectionFactory connections,
    IOptions<WorkerOptions> options,
    ILogger<ExpiredJobRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.RecoveryIntervalSeconds, 10, 600));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await connections.OpenAsync(stoppingToken);
                await using var command = new SqlCommand("dbo.RecoverExpiredJobs", connection);
                var recovered = await command.ExecuteScalarAsync(stoppingToken);
                logger.LogInformation("Expired job recovery completed. Recovered: {Count}", recovered);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Expired job recovery failed."); }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
