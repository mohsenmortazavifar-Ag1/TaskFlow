-- Apply to each tenant database.
-- Tenant-local definitions are the source of truth. Worker projects these rows into central TaskFlow.
IF OBJECT_ID('dbo.ReminderSchedules', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReminderSchedules
    (
        ReminderScheduleId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_TenantReminderSchedules PRIMARY KEY,
        ReminderType nvarchar(100) NOT NULL,
        SourceEntity nvarchar(100) NOT NULL,
        SourceEntityId bigint NOT NULL,
        RuleJson nvarchar(max) NOT NULL,
        TimeZoneId nvarchar(100) NOT NULL,
        ScheduleVersion int NOT NULL CONSTRAINT DF_TenantReminderSchedules_Version DEFAULT(1),
        IsEnabled bit NOT NULL CONSTRAINT DF_TenantReminderSchedules_Enabled DEFAULT(1),
        NextRunAtUtc datetime2(3) NULL,
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_TenantReminderSchedules_Created DEFAULT(SYSUTCDATETIME()),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_TenantReminderSchedules_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT CK_TenantReminderSchedules_RuleJson CHECK (ISJSON(RuleJson) = 1),
        CONSTRAINT CK_TenantReminderSchedules_Version CHECK (ScheduleVersion >= 1)
    );

    CREATE INDEX IX_TenantReminderSchedules_Updated
        ON dbo.ReminderSchedules(UpdatedAt, ReminderScheduleId)
        INCLUDE(IsEnabled, NextRunAtUtc, ScheduleVersion);
END;
GO

-- The application must update UpdatedAt and increment ScheduleVersion whenever
-- reminder content, recurrence, timezone, enabled state, or target entity changes.
-- Disable schedules instead of physically deleting them so the central projection
-- can invalidate pending occurrences safely.
