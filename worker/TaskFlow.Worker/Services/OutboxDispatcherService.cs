using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;
using TaskFlow.Worker.Data;
using TaskFlow.Worker.Models;

namespace TaskFlow.Worker.Services;

public sealed class OutboxDispatcherService(
    TenantRegistryRepository registry,
    TenantSqlConnectionFactory tenantConnections,
    TenantOutboxRepository outbox,
    CentralJobPublisher publisher,
    WorkerHeartbeatService heartbeat,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.Enabled)
        {
            logger.LogInformation("Outbox Dispatcher is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            Math.Clamp(config.PollingIntervalSeconds, 2, 300)));
        logger.LogInformation("Outbox Dispatcher started. Tenant concurrency: {Concurrency}.",
            Math.Clamp(config.MaxTenantConcurrency, 1, 16));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tenants = await registry.GetActiveDatabasesAsync(stoppingToken);
                await Parallel.ForEachAsync(tenants, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(config.MaxTenantConcurrency, 1, 16),
                    CancellationToken = stoppingToken
                }, async (tenant, ct) => await ProcessTenantAsync(tenant, config, ct));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox Dispatcher polling cycle failed.");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessTenantAsync(TenantDatabase tenant, OutboxOptions config, CancellationToken ct)
    {
        try
        {
            await using var connection = await tenantConnections.OpenAsync(tenant, ct);
            var messages = await outbox.ClaimAsync(connection, heartbeat.WorkerId,
                config.BatchSizePerTenant, config.LeaseSeconds, ct);

            foreach (var message in messages)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await publisher.PublishAsync(tenant.TenantId, message, ct);
                    await outbox.MarkPublishedAsync(connection, message.OutboxId, heartbeat.WorkerId, ct);
                    logger.LogDebug("Published outbox {OutboxId} for tenant {TenantId}.",
                        message.OutboxId, tenant.TenantId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Outbox {OutboxId} for tenant {TenantId} failed.",
                        message.OutboxId, tenant.TenantId);
                    await outbox.MarkFailedAsync(connection, message, heartbeat.WorkerId, ex, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not process tenant database {TenantDatabaseId} for tenant {TenantId}.",
                tenant.TenantDatabaseId, tenant.TenantId);
        }
    }
}
