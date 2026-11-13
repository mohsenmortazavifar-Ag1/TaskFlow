-- Apply once to every tenant operational database after 002_reminder_schedules.sql.
-- Rules map domain events (for example RepairCompleted) to reminder definitions.
IF OBJECT_ID('dbo.ReminderEventRules', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReminderEventRules
    (
        ReminderEventRuleId bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_ReminderEventRules PRIMARY KEY,
        EventType nvarchar(100) NOT NULL,
        ReminderType nvarchar(100) NOT NULL,
        SourceEntity nvarchar(100) NOT NULL,
        DelayMinutes int NOT NULL CONSTRAINT DF_ReminderEventRules_Delay DEFAULT(0),
        TimeZoneId nvarchar(100) NOT NULL,
        MobileTemplate nvarchar(100) NOT NULL,
        MessageTemplate nvarchar(2000) NOT NULL,
        ProviderCode nvarchar(50) NULL,
        RecurrenceIntervalValue int NULL,
        RecurrenceIntervalUnit nvarchar(10) NULL,
        LocalTime char(5) NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_ReminderEventRules_Enabled DEFAULT(1),
        CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_ReminderEventRules_Created DEFAULT(SYSUTCDATETIME()),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_ReminderEventRules_Updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT CK_ReminderEventRules_Delay CHECK(DelayMinutes BETWEEN 0 AND 525600),
        CONSTRAINT CK_ReminderEventRules_Recurrence CHECK
        (
            (RecurrenceIntervalValue IS NULL AND RecurrenceIntervalUnit IS NULL)
            OR
            (RecurrenceIntervalValue BETWEEN 1 AND 3650 AND RecurrenceIntervalUnit IN (N'day', N'month', N'year'))
        ),
        CONSTRAINT CK_ReminderEventRules_LocalTime CHECK
        (
            LocalTime IS NULL OR LocalTime LIKE '[0-2][0-9]:[0-5][0-9]'
        )
    );

    CREATE INDEX IX_ReminderEventRules_EventType
        ON dbo.ReminderEventRules(EventType, IsEnabled)
        INCLUDE(ReminderType, SourceEntity, DelayMinutes, TimeZoneId);
END;
GO

-- Inbox provides idempotency: retrying the same eventId must not create duplicate reminders.
IF OBJECT_ID('dbo.ReminderEventInbox', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ReminderEventInbox
    (
        ReminderEventInboxId bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_ReminderEventInbox PRIMARY KEY,
        EventId uniqueidentifier NOT NULL,
        EventType nvarchar(100) NOT NULL,
        SourceEntity nvarchar(100) NOT NULL,
        SourceEntityId bigint NOT NULL,
        OccurredAtUtc datetime2(3) NOT NULL,
        Status tinyint NOT NULL CONSTRAINT DF_ReminderEventInbox_Status DEFAULT(0),
        CreatedReminderCount int NOT NULL CONSTRAINT DF_ReminderEventInbox_Count DEFAULT(0),
        ReceivedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_ReminderEventInbox_Received DEFAULT(SYSUTCDATETIME()),
        ProcessedAtUtc datetime2(3) NULL,
        CONSTRAINT UQ_ReminderEventInbox_EventId UNIQUE(EventId),
        CONSTRAINT CK_ReminderEventInbox_Status CHECK(Status IN (0, 1, 2))
    );
END;
GO

-- Status: 0 processing (transaction-local), 1 processed, 2 no matching enabled rules.
