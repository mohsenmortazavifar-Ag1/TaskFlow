-- Apply to every tenant database after 003_reminder_event_rules.sql.
IF SCHEMA_ID(N'notification') IS NULL EXEC(N'CREATE SCHEMA notification');
IF OBJECT_ID(N'dbo.ReminderEventRules', N'U') IS NOT NULL AND OBJECT_ID(N'notification.ReminderEventRules', N'U') IS NULL
    EXEC(N'ALTER SCHEMA notification TRANSFER dbo.ReminderEventRules');
-- Adds cancellation-event mapping and a finite recurrence limit to event rules.
-- CancelEventType accepts one event code or a comma-separated list of cancellation event codes.
IF COL_LENGTH(N'notification.ReminderEventRules', N'CancelEventType') IS NULL
    ALTER TABLE notification.ReminderEventRules ADD CancelEventType nvarchar(100) NULL;
GO
IF COL_LENGTH(N'notification.ReminderEventRules', N'MaxOccurrences') IS NULL
    ALTER TABLE notification.ReminderEventRules ADD MaxOccurrences int NULL;
GO
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'notification.ReminderEventRules')
      AND name = N'CK_ReminderEventRules_MaxOccurrences'
)
    ALTER TABLE notification.ReminderEventRules
    ADD CONSTRAINT CK_ReminderEventRules_MaxOccurrences
    CHECK (MaxOccurrences IS NULL OR MaxOccurrences BETWEEN 1 AND 1000);
GO
