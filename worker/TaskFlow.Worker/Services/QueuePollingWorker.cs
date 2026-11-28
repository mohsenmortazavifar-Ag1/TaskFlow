using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Handlers;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Services;

public sealed class QueuePollingWorker(
    JobRepository jobs,
    JobHandlerRegistry handlers,
    WorkerHeartbeatService heartbeat,
    IOptions<WorkerOptions> options,
    ILogger<QueuePollingWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _globalSlots = new(Math.Clamp(options.Value.MaxConcurrency, 1, 100));
    private readonly Dictionary<string, SemaphoreSlim> _queueSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, byte> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TaskFlow Worker {WorkerId} started. Max concurrency: {Concurrency}",
            heartbeat.WorkerId, Math.Clamp(options.Value.MaxConcurrency, 1, 100));

        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                var queues = await jobs.GetEnabledQueuesAsync(stoppingToken);
                foreach (var queue in queues)
                {
                    if (!_queueSlots.TryGetValue(queue.QueueName, out var queueSemaphore))
                        _queueSlots[queue.QueueName] = queueSemaphore =
                            new SemaphoreSlim(Math.Clamp(queue.MaxConcurrency, 1, 100));

                    if (!_globalSlots.Wait(0)) break;
                    if (!queueSemaphore.Wait(0))
                    {
                        _globalSlots.Release();
                        continue;
                    }

                    ClaimedJob? job;
                    try { job = await jobs.ClaimAsync(queue.QueueName, heartbeat.WorkerId, stoppingToken); }
                    catch
                    {
                        queueSemaphore.Release();
                        _globalSlots.Release();
                        throw;
                    }

                    if (job is null)
                    {
                        queueSemaphore.Release();
                        _globalSlots.Release();
                        continue;
                    }

                    didWork = true;
                    var task = ProcessAsync(job, queueSemaphore, stoppingToken);
                    _running.TryAdd(task, 0);
                    _ = task.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                if (!didWork)
                    await Task.Delay(Math.Clamp(options.Value.PollingIntervalMilliseconds, 250, 60000), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Queue polling cycle failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        await Task.WhenAll(_running.Keys.ToArray());
    }

    private async Task ProcessAsync(ClaimedJob job, SemaphoreSlim queueSlots, CancellationToken stoppingToken)
    {
        var started = DateTime.UtcNow;
        long? attemptId = null;
        try
        {
            attemptId = await StartAttemptAsync(job, stoppingToken);
            var handler = handlers.Resolve(job.JobType);
            await handler.HandleAsync(job, stoppingToken);
            await jobs.CompleteAsync(job.JobId, heartbeat.WorkerId, stoppingToken);
            await FinishAttemptAsync(attemptId.Value, 1, null, started, stoppingToken);
            logger.LogInformation("Job {JobId} ({JobType}) completed for tenant {TenantId}.",
                job.JobId, job.JobType, job.TenantId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} interrupted during shutdown; its lease will expire.", job.JobId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} ({JobType}) failed.", job.JobId, job.JobType);
            try
            {
                await jobs.FailAsync(job.JobId, heartbeat.WorkerId, ex.ToString(),
                    Math.Clamp(options.Value.RetryDelaySeconds, 1, 3600), CancellationToken.None);
                if (attemptId.HasValue)
                    await FinishAttemptAsync(attemptId.Value, 2, ex.Message, started, CancellationToken.None);
            }
            catch (Exception persistenceError)
            {
                logger.LogCritical(persistenceError, "Could not persist failure for job {JobId}; lease recovery is required.", job.JobId);
            }
        }
        finally
        {
            queueSlots.Release();
            _globalSlots.Release();
        }
    }

    private async Task<long> StartAttemptAsync(ClaimedJob job, CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            INSERT INTO dbo.JobAttempts(JobId, AttemptNumber, WorkerId)
            OUTPUT INSERTED.JobAttemptId
            VALUES(@JobId, @AttemptNumber, @WorkerId);
            """, connection);
        command.Parameters.Add("@JobId", SqlDbType.BigInt).Value = job.JobId;
        command.Parameters.Add("@AttemptNumber", SqlDbType.TinyInt).Value = job.AttemptCount;
        command.Parameters.Add("@WorkerId", SqlDbType.NVarChar, 150).Value = heartbeat.WorkerId;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private async Task FinishAttemptAsync(long attemptId, byte outcome, string? error, DateTime started, CancellationToken ct)
    {
        await using var connection = await jobs.OpenConnectionAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE dbo.JobAttempts
            SET FinishedAt=SYSUTCDATETIME(), Outcome=@Outcome, ErrorMessage=@Error, DurationMs=@Duration
            WHERE JobAttemptId=@AttemptId;
            """, connection);
        command.Parameters.Add("@AttemptId", SqlDbType.BigInt).Value = attemptId;
        command.Parameters.Add("@Outcome", SqlDbType.TinyInt).Value = outcome;
        command.Parameters.Add("@Error", SqlDbType.NVarChar, 2000).Value = (object?)error ?? DBNull.Value;
        command.Parameters.Add("@Duration", SqlDbType.Int).Value = Math.Max(0, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        await command.ExecuteNonQueryAsync(ct);
    }
}
