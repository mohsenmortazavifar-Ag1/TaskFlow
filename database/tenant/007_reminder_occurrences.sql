/*
  Tenant-owned reminder occurrences.
  Apply to every tenant operational database after 002_reminder_schedules.sql
  and 001_outbox_schema.sql.
  Status: 0=Created, 1=ExecutionRequested, 2=SmsQueued, 3=Cancelled, 4=Completed, 5=Failed.
*/
IF SCHEMA_ID(N'notification') IS NULL EXEC(N'CREATE SCHEMA notification');
GO

IF OBJECT_ID(N'notification.ReminderOccurrences', N'U') IS NULL
BEGIN
    CREATE TABLE notification.ReminderOccurrences
    (
        ReminderOccurrenceId BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_TenantReminderOccurrences PRIMARY KEY,
        ReminderScheduleId BIGINT NOT NULL,
        ScheduleVersion INT NOT NULL,
        DueAtUtc DATETIME2(3) NOT NULL,
        Status TINYINT NOT NULL CONSTRAINT DF_TenantReminderOccurrences_Status DEFAULT(0),
        ExecutionEventId UNIQUEIDENTIFIER NOT NULL,
        SmsMessageId BIGINT NULL,
        CreatedAtUtc DATETIME2(3) NOT NULL CONSTRAINT DF_TenantReminderOccurrences_Created DEFAULT(SYSUTCDATETIME()),
        ExecutedAtUtc DATETIME2(3) NULL,
        LastError NVARCHAR(1000) NULL,
        CONSTRAINT FK_TenantReminderOccurrences_Schedule
            FOREIGN KEY(ReminderScheduleId) REFERENCES notification.ReminderSchedules(ReminderScheduleId),
        CONSTRAINT UQ_TenantReminderOccurrences_Due
            UNIQUE(ReminderScheduleId, ScheduleVersion, DueAtUtc),
        CONSTRAINT UQ_TenantReminderOccurrences_Event
            UNIQUE(ExecutionEventId),
        CONSTRAINT CK_TenantReminderOccurrences_Status CHECK(Status BETWEEN 0 AND 5),
        CONSTRAINT CK_TenantReminderOccurrences_Version CHECK(ScheduleVersion >= 1)
    );

    CREATE INDEX IX_TenantReminderOccurrences_Status_Due
        ON notification.ReminderOccurrences(Status, DueAtUtc, ReminderOccurrenceId)
        INCLUDE(ReminderScheduleId, ScheduleVersion, ExecutionEventId, SmsMessageId);
END;
GO
