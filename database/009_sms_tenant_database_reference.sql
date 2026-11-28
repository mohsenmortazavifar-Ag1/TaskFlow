/*
  Store the owning tenant database on central SMS records.
  Apply to TaskFlow central database.
*/
IF COL_LENGTH(N'dbo.SmsMessages', N'TenantDatabaseId') IS NULL
    ALTER TABLE dbo.SmsMessages ADD TenantDatabaseId BIGINT NULL;
GO
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SmsMessages')
      AND name = N'IX_SmsMessages_TenantDatabaseId_Delivery'
)
    CREATE INDEX IX_SmsMessages_TenantDatabaseId_Delivery
        ON dbo.SmsMessages(TenantDatabaseId, SubmissionState, DeliveryState, NextDeliveryCheckAt)
        INCLUDE(TenantId, ProviderMessageId, SmsMessageId);
GO
