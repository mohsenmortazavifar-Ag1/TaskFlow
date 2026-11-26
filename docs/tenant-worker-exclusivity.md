# Tenant worker exclusivity and processing rules

## Non-negotiable rule

At any instant, at most one Worker process may perform tenant-scoped background work against a given `TenantDatabaseId`. This applies across multiple Worker instances and also serializes the hosted services inside one Worker process.

## Lock implementation

- `TenantDatabaseLock` uses SQL Server `sys.sp_getapplock`.
- Resource name is stable per tenant database: `TaskFlow:TenantWorker:{TenantDatabaseId}:Exclusive`.
- Lock mode is `Exclusive`, owner is `Session`, and the lock is acquired on the same open tenant SQL connection used for the protected work.
- The connection must remain open until the entire protected operation completes.
- Closing/breaking the SQL session releases the lock automatically. A second Worker waits and then acquires it.
- A timeout is contention, not a processing failure. The acquisition loop waits until cancellation or lock grant.
- Never use a connection from another tenant database for the same lock resource.
- Never return a tenant connection to the pool while its lock is held; dispose the lease before disposing the connection.

## Protected operations

Every tenant-scoped Worker workflow must acquire this lock before reading or writing tenant operational data and hold it through the full operation:

1. Consume tenant Outbox messages and publish them to central `TaskFlow`.
2. Process `ReminderEventInbox` events and create/cancel schedules.
3. Find due `ReminderSchedules`, create `ReminderOccurrences`, advance schedule cursors and insert execution Outbox messages.
4. Execute a reminder occurrence and create its central SMS job/message.
5. Send an SMS after validating current tenant reminder state, and persist provider results in both central and tenant stores.
6. Poll provider delivery status and persist delivery updates to the owning tenant.

Do not hold one tenant lock while processing a different tenant. Different tenants may run concurrently, subject to bounded concurrency.

## Failure and idempotency rules

- The lock is not a replacement for idempotency. `ReminderEventInbox.EventId`, occurrence uniqueness, tenant Outbox `EventId`, central Job `IdempotencyKey`, and SMS message idempotency must all remain enforced by unique constraints.
- No distributed SQL transaction is used. Tenant state and tenant Outbox are committed atomically in the tenant database. Central publication is retryable and idempotent.
- Once a provider request may have reached the provider, an ambiguous result must never cause an automatic resend. Persist an unknown/needs-review state and reconcile it.
- If provider acceptance is persisted centrally but the tenant delivery-log write fails, retry only the tenant log write. Do not send the SMS again.
- Tenant DB remains source of truth for reminder definitions, occurrences, cancellation and execution state. Central TaskFlow owns worker queue and SMS dispatch state.

## Future billing compatibility

Central SMS records should carry a stable tenant-scoped idempotency key and immutable pricing snapshot fields (`SegmentCount`, `UnitPrice`, `TotalCost`, `PricingVersion`, `BillingStatus`). Future wallet balance and ledger transactions belong in central TaskFlow. Environment variables may configure pricing policy/rates, but must never represent a tenant balance or financial ledger.

## Operational validation

Run at least two Worker instances against the same SQL Server and verify:
- only one instance logs acquisition and processes a selected TenantDatabaseId at a time;
- a second instance waits while the first holds the tenant lock;
- stopping/killing the first instance closes its SQL session and allows the second to proceed;
- different TenantDatabaseIds can still be processed concurrently;
- lock contention does not consume Job/Outbox retry attempts;
- process restart and connection loss do not duplicate ReminderOccurrences or provider submissions.
