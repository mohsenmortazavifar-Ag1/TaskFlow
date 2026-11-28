# TaskFlow database migrations

TaskFlow has two separate database migration streams. Never execute a tenant migration against the central TaskFlow database, or a central migration against a Repair tenant database.

## 1. Central TaskFlow database

The central database owns Job queues, SMS messages, delivery tracking and the tenant SMS log synchronization outbox.

### Fresh installation

Run the bootstrap scripts in this order:

1. `001_create_database.sql` — create the central database.
2. `002_schema.sql` — central tables and base schema.
3. `003_seed.sql` — required seed data.
4. `004_procedures.sql` — queue and job stored procedures.
5. `006_sms_delivery_tracking.sql` — SMS submission/delivery state.
6. `008_sms_idempotency_billing_foundation.sql` — SMS idempotency and billing snapshot foundation.
7. `009_sms_tenant_database_reference.sql` — tenant database references.
8. `010_sms_tenant_log_outbox.sql` — durable tenant SMS log synchronization.

Files 005 and 007 are historical central Reminder migrations. They are not part of a fresh installation because Reminder ownership moved to tenant operational databases.

### Existing central databases

- Do not rerun bootstrap scripts blindly against a populated database.
- Apply only central migrations missing from that database, after reviewing the existing schema and release notes.
- `011_archive_legacy_central_reminders.sql` is only for databases where the old central Reminder projection/sync migrations (005 and/or 007) were previously applied. First verify that required Reminder data has been moved to the owning tenant database. The archive migration preserves the old tables under archive names; it is not part of fresh setup.
- Keep the historical files 005 and 007 unchanged. Their presence in Git does not mean they should be executed in the current architecture.

## 2. Tenant operational databases

The ordered tenant migration chain is maintained in `database/tenant/`. It is manually applied in the TaskFlow repository for standalone/legacy tenant rollout. The SaaS tenant provisioning source of truth is the versioned chain in `managerProject/database/tenant-migrations/`; do not independently run both chains on the same tenant.

| Order | File | Purpose |
|---|---|---|
| 001 | `001_outbox_schema.sql` | Tenant transactional Outbox |
| 002 | `002_reminder_schedules.sql` | Tenant-owned reminder schedules |
| 003 | `003_reminder_event_rules.sql` | Event rules and idempotent event inbox |
| 004 | `004_reminder_event_cancellation_limits.sql` | Cancellation mapping and occurrence limits |
| 005 | `005_seed_repair_completed_rule.sql` | Seed the RepairCompleted follow-up rule |
| 006 | `006_move_reminder_tables_to_notification.sql` | Legacy dbo-to-notification schema transfer |
| 007 | `007_order_selected_service_reminder_delay.sql` | One-time 3/6/12-month delay selected per repair order |
| 008 | `008_reminder_occurrences.sql` | Materialized reminder occurrences |
| 009 | `009_sms_delivery_logs.sql` | Tenant-local SMS delivery history |
| 010 | `010_reminder_finite_recurrence.sql` | Finite recurrence enforcement |
| 011 | `011_update_service_reminder_message_template.sql` | Current RepairCompleted template |

Run them in ascending order. For new tenants, 006 normally has no legacy tables to transfer; for existing tenants, it intentionally stops if the same table exists in both dbo and notification. Reconcile the data before retrying.

The seed and template migrations update tenant-owned data, not central TaskFlow data. Tenant-specific message content, provider configuration and sender settings must be reviewed before rollout.

## Versioning rules

- Keep the central historical filenames immutable.
- Tenant scripts use a single consecutive numeric sequence (001–011). Do not reuse a number or leave a gap.
- When a tenant schema change is required by the SaaS provisioning flow, add it to the canonical versioned chain in managerProject first. Keep the TaskFlow tenant copy aligned only when it is still needed for standalone/legacy rollout.
- Do not edit a migration that has already been executed in production. Add a new migration and document its dependency.
- Back up databases and test migrations against a restored tenant/central database before production.
