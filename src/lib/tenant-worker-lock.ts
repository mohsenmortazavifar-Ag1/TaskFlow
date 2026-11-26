import {sql} from "@/lib/db";

/**
 * Shares the exact exclusive resource used by the .NET Worker. API mutations
 * therefore cannot race a worker's reminder validation or execution.
 * Transaction ownership releases the lock automatically on commit/rollback.
 */
export async function acquireTenantWorkerLock(
  transaction: sql.Transaction,
  tenantDatabaseId: number
): Promise<void> {
  if (!Number.isSafeInteger(tenantDatabaseId) || tenantDatabaseId <= 0)
    throw new Error("Invalid tenantDatabaseId for tenant worker lock.");

  await new sql.Request(transaction)
    .input("Resource", sql.NVarChar(255), `TaskFlow:TenantWorker:${tenantDatabaseId}:Exclusive`)
    .query(`
      DECLARE @LockResult int;
      EXEC @LockResult = sys.sp_getapplock
        @Resource = @Resource,
        @LockMode = N'Exclusive',
        @LockOwner = N'Transaction',
        @LockTimeout = 10000,
        @DbPrincipal = N'public';
      IF @LockResult < 0
        THROW 51020, 'TENANT_WORKER_LOCK_TIMEOUT', 1;
    `);
}
