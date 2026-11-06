-- Apply to the central TaskFlow database.
-- Adds a stable mapping from each tenant-local reminder definition to its central projection.
IF COL_LENGTH('dbo.ReminderSchedules', 'TenantDatabaseId') IS NULL
    ALTER TABLE dbo.ReminderSchedules ADD TenantDatabaseId bigint NULL;
GO
IF COL_LENGTH('dbo.ReminderSchedules', 'TenantReminderScheduleId') IS NULL
    ALTER TABLE dbo.ReminderSchedules ADD TenantReminderScheduleId bigint NULL;
GO
IF COL_LENGTH('dbo.ReminderSchedules', 'SourceUpdatedAt') IS NULL
    ALTER TABLE dbo.ReminderSchedules ADD SourceUpdatedAt datetime2(3) NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.ReminderSchedules') AND name = 'UX_ReminderSchedules_TenantSource')
    CREATE UNIQUE INDEX UX_ReminderSchedules_TenantSource
        ON dbo.ReminderSchedules(TenantDatabaseId, TenantReminderScheduleId)
        WHERE TenantDatabaseId IS NOT NULL AND TenantReminderScheduleId IS NOT NULL;
GO
IF OBJECT_ID('dbo.TenantReminderSyncState', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantReminderSyncState
    (
        TenantDatabaseId bigint NOT NULL PRIMARY KEY,
        LastSyncedAtUtc datetime2(3) NULL,
        LastSyncAtUtc datetime2(3) NULL,
        LastError nvarchar(2000) NULL
    );
END;
GO
