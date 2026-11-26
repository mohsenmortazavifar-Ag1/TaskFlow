/*
  Durable outbox marker for copying central SMS delivery events into tenant notification.SmsLogs.
*/
IF COL_LENGTH(N'dbo.SmsLogs', N'TenantSyncedAt') IS NULL
    ALTER TABLE dbo.SmsLogs ADD TenantSyncedAt DATETIME2(3) NULL;
GO
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SmsLogs')
      AND name = N'IX_SmsLogs_TenantSync'
)
    CREATE INDEX IX_SmsLogs_TenantSync
        ON dbo.SmsLogs(TenantSyncedAt, SmsLogId)
        INCLUDE(SmsMessageId, EventType);
GO

GO
IF COL_LENGTH(N'dbo.SmsLogs', N'TenantSyncNextAttemptAt') IS NULL
    ALTER TABLE dbo.SmsLogs ADD TenantSyncNextAttemptAt DATETIME2(3) NULL;
GO
IF COL_LENGTH(N'dbo.SmsLogs', N'TenantSyncLastError') IS NULL
    ALTER TABLE dbo.SmsLogs ADD TenantSyncLastError NVARCHAR(1000) NULL;
GO
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SmsLogs')
      AND name = N'IX_SmsLogs_TenantSyncRetry'
)
    CREATE INDEX IX_SmsLogs_TenantSyncRetry
        ON dbo.SmsLogs(TenantSyncedAt, TenantSyncNextAttemptAt, SmsLogId)
        INCLUDE(SmsMessageId, EventType);
GO
