/*
  Retire the legacy central Reminder projection without losing its historical data.
  Apply once to the central TaskFlow database after deploying the Tenant-owned pipeline.
  New installations do not create these tables and must not run migrations 005 or 007.
*/
IF DB_NAME() <> N'TaskFlow'
    THROW 50040, 'Run this migration in the central TaskFlow database.', 1;
GO

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.ReminderOccurrences', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'dbo.Legacy_ReminderOccurrences_Archive', N'U') IS NOT NULL
        THROW 50041, 'Both legacy ReminderOccurrences and its archive already exist; reconcile manually.', 1;
    EXEC sys.sp_rename N'dbo.ReminderOccurrences', N'Legacy_ReminderOccurrences_Archive';
END;

IF OBJECT_ID(N'dbo.ReminderSchedules', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'dbo.Legacy_ReminderSchedules_Archive', N'U') IS NOT NULL
        THROW 50042, 'Both legacy ReminderSchedules and its archive already exist; reconcile manually.', 1;
    EXEC sys.sp_rename N'dbo.ReminderSchedules', N'Legacy_ReminderSchedules_Archive';
END;

IF OBJECT_ID(N'dbo.TenantReminderSyncState', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'dbo.Legacy_TenantReminderSyncState_Archive', N'U') IS NOT NULL
        THROW 50043, 'Both legacy TenantReminderSyncState and its archive already exist; reconcile manually.', 1;
    EXEC sys.sp_rename N'dbo.TenantReminderSyncState', N'Legacy_TenantReminderSyncState_Archive';
END;

COMMIT TRANSACTION;
GO
