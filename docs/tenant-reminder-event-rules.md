# Tenant reminder event rules

Apply `database/tenant/004_reminder_event_cancellation_limits.sql` after the existing tenant reminder-event migrations. Apply `database/006_reminder_schedule_occurrence_count.sql` to the central TaskFlow database before deploying the updated Worker.

## Event contracts

Repair publishes reminder domain events through its tenant Outbox:

- `RepairCompleted`: repair work completed; use this for periodic-service reminders.
- `ReadyForDelivery`: device is ready for customer pickup; use this for pickup reminders.
- `RepairDelivered`: device was handed to the customer; cancel pickup reminders.
- `RepairCancelled`: repair order was cancelled.
- `RepairStatusChanged`: an order that had reached a reminder-triggering state was moved back to repair.

All events identify the source as `RepairOrder` and use the RepairOrderId as `SourceEntityId`.

## Example pickup rule

This is an example only; choose the actual delay, recurrence, local time and limit for each tenant. The current recurrence model supports a uniform interval (for example every 1 day), not a non-uniform sequence such as day 1, 3 and 7.

```sql
UPDATE dbo.ReminderEventRules
SET EventType = N'ReadyForDelivery',
    ReminderType = N'PickupReminder',
    SourceEntity = N'RepairOrder',
    DelayMinutes = 1440,
    TimeZoneId = N'Asia/Tehran',
    MobileTemplate = N'{{customerMobile}}',
    MessageTemplate = N'{{customerName}} عزیز، دستگاه شما آماده تحویل است. شماره پذیرش: {{repairOrderNumber}}',
    RecurrenceIntervalValue = 1,
    RecurrenceIntervalUnit = N'day',
    LocalTime = '10:00',
    MaxOccurrences = 3,
    CancelEventType = N'RepairDelivered,RepairCancelled,RepairStatusChanged',
    IsEnabled = 1,
    UpdatedAt = SYSUTCDATETIME()
WHERE ReminderEventRuleId = <existing-rule-id>;
```

Replace the rule ID and message with the tenant's actual rule and approved text. Do not execute this sample as-is.

## Execution semantics

- `MaxOccurrences` counts occurrences materialized by the central Scheduler, including the first due occurrence. For example, 3 means at most three scheduled reminder jobs.
- SMS provider retries happen in the SMS job pipeline; they do not create additional reminder occurrences.
- `LocalTime` is applied to the first due date after the configured delay and to subsequent recurring dates using `TimeZoneId`.
- `CancelEventType` accepts one event code or a comma-separated list. A matching cancellation event disables the schedule, clears its next run time and increments `ScheduleVersion`, invalidating queued-but-not-yet-executed occurrences.
- Rule edits should increment the tenant schedule's `ScheduleVersion` through the reminder API so the central execution cursor and occurrence count are reset intentionally.
