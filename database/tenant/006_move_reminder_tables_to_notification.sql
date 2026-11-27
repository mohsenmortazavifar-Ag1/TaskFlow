/*
  Move legacy tenant reminder/outbox tables from dbo to notification.
  Apply once to every tenant operational database after the reminder migrations.
  The transfer preserves table data, identity values, indexes, constraints and permissions.
*/
SET XACT_ABORT ON;

IF SCHEMA_ID(N'notification') IS NULL
    EXEC(N'CREATE SCHEMA notification');

IF OBJECT_ID(N'dbo.OutboxMessages', N'U') IS NOT NULL
   AND OBJECT_ID(N'notification.OutboxMessages', N'U') IS NOT NULL
BEGIN
    ;THROW 51010, 'Both dbo.OutboxMessages and notification.OutboxMessages exist. Reconcile them before migration.', 1;
END;
IF OBJECT_ID(N'dbo.ReminderSchedules', N'U') IS NOT NULL
   AND OBJECT_ID(N'notification.ReminderSchedules', N'U') IS NOT NULL
BEGIN
    ;THROW 51011, 'Both dbo.ReminderSchedules and notification.ReminderSchedules exist. Reconcile them before migration.', 1;
END;
IF OBJECT_ID(N'dbo.ReminderEventRules', N'U') IS NOT NULL
   AND OBJECT_ID(N'notification.ReminderEventRules', N'U') IS NOT NULL
BEGIN
    ;THROW 51012, 'Both dbo.ReminderEventRules and notification.ReminderEventRules exist. Reconcile them before migration.', 1;
END;
IF OBJECT_ID(N'dbo.ReminderEventInbox', N'U') IS NOT NULL
   AND OBJECT_ID(N'notification.ReminderEventInbox', N'U') IS NOT NULL
BEGIN
    ;THROW 51013, 'Both dbo.ReminderEventInbox and notification.ReminderEventInbox exist. Reconcile them before migration.', 1;
END;

IF OBJECT_ID(N'dbo.OutboxMessages', N'U') IS NOT NULL
    EXEC(N'ALTER SCHEMA notification TRANSFER dbo.OutboxMessages');
IF OBJECT_ID(N'dbo.ReminderSchedules', N'U') IS NOT NULL
    EXEC(N'ALTER SCHEMA notification TRANSFER dbo.ReminderSchedules');
IF OBJECT_ID(N'dbo.ReminderEventRules', N'U') IS NOT NULL
    EXEC(N'ALTER SCHEMA notification TRANSFER dbo.ReminderEventRules');
IF OBJECT_ID(N'dbo.ReminderEventInbox', N'U') IS NOT NULL
    EXEC(N'ALTER SCHEMA notification TRANSFER dbo.ReminderEventInbox');

IF OBJECT_ID(N'notification.OutboxMessages', N'U') IS NOT NULL
   AND OBJECT_ID(N'notification.ReminderSchedules', N'U') IS NOT NULL
   AND OBJECT_ID(N'notification.ReminderEventRules', N'U') IS NOT NULL
   AND OBJECT_ID(N'notification.ReminderEventInbox', N'U') IS NOT NULL
    PRINT 'Tenant notification schema migration completed.';
GO
