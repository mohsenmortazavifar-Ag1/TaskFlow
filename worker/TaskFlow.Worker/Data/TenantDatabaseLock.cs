using System.Data;
using Microsoft.Data.SqlClient;

namespace TaskFlow.Worker.Data;

/// <summary>
/// Acquires an exclusive, database-scoped SQL application lock for a tenant database.
/// The lock is owned by the SQL session and is automatically released if the connection
/// is closed or the worker process dies. Every worker path that reads or writes tenant
/// operational data must hold this lease for the full operation.
/// </summary>
public sealed class TenantDatabaseLock(ILogger<TenantDatabaseLock> logger)
{
    public async Task<IAsyncDisposable> AcquireAsync(
        SqlConnection connection,
        long tenantDatabaseId,
        string workerId,
        CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Tenant lock requires an open SQL connection.");
        if (tenantDatabaseId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantDatabaseId));

        var resource = $"TaskFlow:TenantWorker:{tenantDatabaseId}:Exclusive";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = new SqlCommand("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = @Resource,
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Session',
                    @LockTimeout = 5000,
                    @DbPrincipal = N'public';
                SELECT @result;
                """, connection) { CommandTimeout = 10 };
            command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;

            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (result >= 0)
            {
                logger.LogDebug(
                    "Worker {WorkerId} acquired exclusive tenant database lock for TenantDatabaseId {TenantDatabaseId}.",
                    workerId, tenantDatabaseId);
                return new TenantDatabaseLockLease(connection, resource, workerId, tenantDatabaseId, logger);
            }

            // -1 means timeout: another worker currently owns this tenant.
            // Keep waiting rather than failing the job and consuming its retry budget.
            if (result == -1)
                continue;

            throw new InvalidOperationException(
                $"Could not acquire tenant database lock for TenantDatabaseId={tenantDatabaseId}. sp_getapplock returned {result}.");
        }
    }

    private sealed class TenantDatabaseLockLease(
        SqlConnection connection,
        string resource,
        string workerId,
        long tenantDatabaseId,
        ILogger logger) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (connection.State != ConnectionState.Open)
                return; // Closing the SQL session has already released its session-owned lock.

            try
            {
                await using var command = new SqlCommand("""
                    EXEC sys.sp_releaseapplock
                        @Resource = @Resource,
                        @LockOwner = N'Session',
                        @DbPrincipal = N'public';
                    """, connection) { CommandTimeout = 10 };
                command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
                await command.ExecuteNonQueryAsync(CancellationToken.None);
                logger.LogDebug(
                    "Worker {WorkerId} released exclusive tenant database lock for TenantDatabaseId {TenantDatabaseId}.",
                    workerId, tenantDatabaseId);
            }
            catch (Exception ex)
            {
                // Connection disposal immediately after this lease will release the lock.
                logger.LogWarning(ex,
                    "Could not explicitly release tenant lock for TenantDatabaseId {TenantDatabaseId}; connection disposal will release it.",
                    tenantDatabaseId);
            }
        }
    }
}
