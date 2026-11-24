/*
  Support a one-shot service reminder whose delay is selected per repair order.
  Apply to each tenant operational database after 003_reminder_event_rules.sql
  and 004_reminder_event_cancellation_limits.sql.
*/
SET XACT_ABORT ON;

IF COL_LENGTH(N'notification.ReminderEventRules', N'DelayDataKey') IS NULL
  ALTER TABLE notification.ReminderEventRules ADD DelayDataKey NVARCHAR(100) NULL;
GO

IF COL_LENGTH(N'notification.ReminderEventRules', N'DelayUnit') IS NULL
  ALTER TABLE notification.ReminderEventRules ADD DelayUnit NVARCHAR(10) NULL;
GO

IF NOT EXISTS (
  SELECT 1 FROM sys.check_constraints
  WHERE parent_object_id = OBJECT_ID(N'notification.ReminderEventRules')
    AND name = N'CK_ReminderEventRules_DynamicDelay'
)
  ALTER TABLE notification.ReminderEventRules
  ADD CONSTRAINT CK_ReminderEventRules_DynamicDelay CHECK (
    (DelayDataKey IS NULL AND DelayUnit IS NULL)
    OR (DelayDataKey IS NOT NULL AND DelayUnit = N'month')
  );
GO

-- Replace the previous fixed 3-day sample with an opt-in, one-time delay
-- supplied by Repair as serviceReminderIntervalMonths (3, 6, or 12).
UPDATE notification.ReminderEventRules
SET DelayMinutes = 0,
    DelayDataKey = N'serviceReminderIntervalMonths',
    DelayUnit = N'month',
    RecurrenceIntervalValue = NULL,
    RecurrenceIntervalUnit = NULL,
    MaxOccurrences = 1,
    CancelEventType = N'RepairStatusChanged,RepairDelivered,RepairCancelled',
    UpdatedAt = SYSUTCDATETIME()
WHERE EventType = N'RepairCompleted'
  AND ReminderType = N'RepairCompletedFollowUp'
  AND SourceEntity = N'RepairOrder';

-- Invalidate previously scheduled fixed-delay follow-ups. Only events created
-- after this migration can carry the order-specific interval and create a new schedule.
UPDATE schedule
SET IsEnabled = 0,
    NextRunAtUtc = NULL,
    ScheduleVersion = ScheduleVersion + 1,
    UpdatedAt = SYSUTCDATETIME()
FROM notification.ReminderSchedules AS schedule
WHERE schedule.IsEnabled = 1
  AND JSON_VALUE(schedule.RuleJson, '$.ruleId') IN (
    SELECT CONVERT(nvarchar(20), ReminderEventRuleId)
    FROM notification.ReminderEventRules
    WHERE EventType = N'RepairCompleted'
      AND ReminderType = N'RepairCompletedFollowUp'
      AND SourceEntity = N'RepairOrder'
  );
GO
