-- Apply to every tenant database after 003_reminder_event_rules.sql.
-- Adds cancellation-event mapping and a finite recurrence limit to event rules.
-- CancelEventType accepts one event code or a comma-separated list of cancellation event codes.
IF COL_LENGTH(N'dbo.ReminderEventRules', N'CancelEventType') IS NULL
    ALTER TABLE dbo.ReminderEventRules ADD CancelEventType nvarchar(100) NULL;
GO
IF COL_LENGTH(N'dbo.ReminderEventRules', N'MaxOccurrences') IS NULL
    ALTER TABLE dbo.ReminderEventRules ADD MaxOccurrences int NULL;
GO
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.ReminderEventRules')
      AND name = N'CK_ReminderEventRules_MaxOccurrences'
)
    ALTER TABLE dbo.ReminderEventRules
    ADD CONSTRAINT CK_ReminderEventRules_MaxOccurrences
    CHECK (MaxOccurrences IS NULL OR MaxOccurrences BETWEEN 1 AND 1000);
GO
