/*
  Tenant-side SMS delivery history mirror.
  Apply to every tenant database after notification schema migrations.
  TaskFlowSmsMessageId + EventType is the idempotency boundary for central-to-tenant log replication.
*/
IF SCHEMA_ID(N'notification') IS NULL EXEC(N'CREATE SCHEMA notification');
GO

IF OBJECT_ID(N'notification.SmsLogs', N'U') IS NULL
BEGIN
    CREATE TABLE notification.SmsLogs
    (
        SmsLogId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TenantSmsLogs PRIMARY KEY,
        TemplateId BIGINT NULL,
        Mobile NVARCHAR(20) NOT NULL,
        Message NVARCHAR(2000) NOT NULL,
        Status NVARCHAR(30) NOT NULL,
        ProviderMessageId NVARCHAR(300) NULL,
        ErrorMessage NVARCHAR(2000) NULL,
        SentAt DATETIME2(3) NULL,
        CreatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_TenantSmsLogs_CreatedAt DEFAULT(SYSUTCDATETIME()),
        TaskFlowSmsMessageId BIGINT NULL,
        EventType NVARCHAR(50) NULL,
        ProviderCode NVARCHAR(100) NULL,
        ProviderStatus INT NULL
    );
END;
GO

IF COL_LENGTH(N'notification.SmsLogs', N'TaskFlowSmsMessageId') IS NULL
    ALTER TABLE notification.SmsLogs ADD TaskFlowSmsMessageId BIGINT NULL;
GO
IF COL_LENGTH(N'notification.SmsLogs', N'EventType') IS NULL
    ALTER TABLE notification.SmsLogs ADD EventType NVARCHAR(50) NULL;
GO
IF COL_LENGTH(N'notification.SmsLogs', N'ProviderCode') IS NULL
    ALTER TABLE notification.SmsLogs ADD ProviderCode NVARCHAR(100) NULL;
GO
IF COL_LENGTH(N'notification.SmsLogs', N'ProviderStatus') IS NULL
    ALTER TABLE notification.SmsLogs ADD ProviderStatus INT NULL;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'notification.SmsLogs')
      AND name = N'UX_TenantSmsLogs_TaskFlowMessage_Event'
)
    CREATE UNIQUE INDEX UX_TenantSmsLogs_TaskFlowMessage_Event
        ON notification.SmsLogs(TaskFlowSmsMessageId, EventType)
        WHERE TaskFlowSmsMessageId IS NOT NULL AND EventType IS NOT NULL;
GO
