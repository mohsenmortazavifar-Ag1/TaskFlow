# Tenant reminder event rules

Apply `database/tenant/005_move_reminder_tables_to_notification.sql` to every tenant database to migrate legacy `dbo` tables. The tenant outbox, reminder schedules, event rules and event inbox are stored in the `notification` schema. For a fresh tenant, run the tenant migrations in order; they create these tables directly in `notification`. Apply `database/007_reminder_schedule_occurrence_count.sql` to the central TaskFlow database before deploying the updated Worker.

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
UPDATE notification.ReminderEventRules
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
- Changing a row in `ReminderEventRules` affects schedules created by future events; it does not rewrite existing schedules. Edit or cancel an existing schedule through the reminder API, which increments that schedule's `ScheduleVersion` and invalidates older queued occurrences.

## Repair-order-selected one-time service reminder

For `RepairCompletedFollowUp`, migration `database/tenant/006_order_selected_service_reminder_delay.sql` changes the rule to read `serviceReminderIntervalMonths` from the event payload. Repair sends this value only when the operator enabled the reminder for that repair order. Supported values are 3, 6, and 12 calendar months.

The due date is calculated from the event's `occurredAtUtc` in the rule's `TimeZoneId`, clamping month-end dates (for example, November 30 plus three months becomes February 28/29). The schedule is one-shot: no recurrence interval is stored, so the scheduler clears `NextRunAtUtc` after materializing its single occurrence. The rule also uses `MaxOccurrences = 1`.

Apply `004_seed_repair_completed_rule.sql` first, then `006_order_selected_service_reminder_delay.sql` to every Repair tenant database. The latter invalidates previously projected fixed-delay schedules for this rule; the tenant reminder sync must run so the central scheduler projection receives the disabled state.

Repair emits the selected interval only on the `RepairCompleted` event created by the transition to `Repaired`. If the order preference is disabled, the dynamic rule is skipped and no follow-up schedule is created. Moving the order back to repair, delivering it, or cancelling it disables any active schedule.