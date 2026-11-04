using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;

namespace TaskFlow.Worker.Services;

public sealed class WorkerHeartbeatService(
    SqlConnectionFactory connections,
    IOptions<WorkerOptions> options,
    ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    private readonly string _workerId = string.IsNullOrWhiteSpace(options.Value.WorkerId)
        ? $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..Math.Min(150, Environment.MachineName.Length + 1 + Environment.ProcessId.ToString().Length + 1 + 32)]
        : options.Value.WorkerId;

    public string WorkerId => _workerId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await UpdateAsync(1, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.HeartbeatIntervalSeconds, 5, 120)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await UpdateAsync(1, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try { await UpdateAsync(0, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not mark worker {WorkerId} as stopped.", _workerId); }
        }
    }

    private async Task UpdateAsync(byte status, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = new SqlCommand("""
            MERGE dbo.WorkerInstances AS target
            USING (SELECT @WorkerId AS WorkerId) AS source
            ON target.WorkerId = source.WorkerId
            WHEN MATCHED THEN UPDATE SET HostName=@HostName, ProcessId=@ProcessId,
                Version=@Version, Status=@Status, LastHeartbeatAt=SYSUTCDATETIME(),
                StoppedAt=CASE WHEN @Status=0 THEN SYSUTCDATETIME() ELSE NULL END
            WHEN NOT MATCHED THEN INSERT(WorkerId,HostName,ProcessId,Version,Status,StartedAt,LastHeartbeatAt)
                VALUES(@WorkerId,@HostName,@ProcessId,@Version,@Status,SYSUTCDATETIME(),SYSUTCDATETIME());
            """, connection);
        command.Parameters.Add("@WorkerId", SqlDbType.NVarChar, 150).Value = _workerId;
        command.Parameters.Add("@HostName", SqlDbType.NVarChar, 150).Value = Environment.MachineName;
        command.Parameters.Add("@ProcessId", SqlDbType.Int).Value = Environment.ProcessId;
        command.Parameters.Add("@Version", SqlDbType.NVarChar, 30).Value = typeof(WorkerHeartbeatService).Assembly.GetName().Version?.ToString() ?? "1.0";
        command.Parameters.Add("@Status", SqlDbType.TinyInt).Value = status;
        await command.ExecuteNonQueryAsync(ct);
        logger.LogDebug("Worker {WorkerId} heartbeat updated.", _workerId);
    }
}
