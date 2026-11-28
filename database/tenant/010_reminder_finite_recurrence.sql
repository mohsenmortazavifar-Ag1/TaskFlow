/*
  Require a finite number of occurrences for recurring tenant reminders.
  Existing recurring rules with no limit are capped at 12 occurrences as a safe,
  explicit finite default. Operators may choose a lower or higher limit (max 1000).
  Apply to every tenant database after 004_reminder_event_cancellation_limits.sql.
*/
SET XACT_ABORT ON;
GO

UPDATE notification.ReminderEventRules
SET MaxOccurrences = 12,
    UpdatedAt = SYSUTCDATETIME()
WHERE RecurrenceIntervalValue IS NOT NULL
  AND RecurrenceIntervalUnit IS NOT NULL
  AND MaxOccurrences IS NULL;
GO

-- Bound existing manually-created schedules as well; they may predate the API validation.
UPDATE notification.ReminderSchedules
SET RuleJson = JSON_MODIFY(RuleJson, '$.maxOccurrences', 12),
    UpdatedAt = SYSUTCDATETIME()
WHERE TRY_CONVERT(INT, JSON_VALUE(RuleJson, '$.intervalValue')) IS NOT NULL
  AND JSON_VALUE(RuleJson, '$.maxOccurrences') IS NULL;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'notification.ReminderEventRules')
      AND name = N'CK_ReminderEventRules_RecurrenceRequiresFiniteMax'
)
    ALTER TABLE notification.ReminderEventRules
    ADD CONSTRAINT CK_ReminderEventRules_RecurrenceRequiresFiniteMax
    CHECK
    (
        (RecurrenceIntervalValue IS NULL AND RecurrenceIntervalUnit IS NULL)
        OR
        (RecurrenceIntervalValue IS NOT NULL AND RecurrenceIntervalUnit IS NOT NULL
         AND MaxOccurrences IS NOT NULL AND MaxOccurrences BETWEEN 1 AND 1000)
    );
GO
