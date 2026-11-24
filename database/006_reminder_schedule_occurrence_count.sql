-- Apply to the central TaskFlow database.
-- OccurrenceCount is central execution state and must never be sourced from tenant snapshots.
IF COL_LENGTH(N'dbo.ReminderSchedules', N'OccurrenceCount') IS NULL
BEGIN
    ALTER TABLE dbo.ReminderSchedules
    ADD OccurrenceCount int NOT NULL
        CONSTRAINT DF_ReminderSchedules_OccurrenceCount DEFAULT (0) WITH VALUES;
END;
GO
