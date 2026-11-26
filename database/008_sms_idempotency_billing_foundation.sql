/*
  SMS idempotency and future billing snapshot fields.
  Apply to the central TaskFlow database.
  Billing is intentionally not activated by this migration.
*/
IF DB_NAME() <> N'TaskFlow'
    THROW 50030, 'Run this migration in the TaskFlow database.', 1;
GO

IF COL_LENGTH(N'dbo.SmsMessages', N'IdempotencyKey') IS NULL
    ALTER TABLE dbo.SmsMessages ADD IdempotencyKey NVARCHAR(200) NULL;
GO

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.SmsMessages')
      AND name = N'UX_SmsMessages_Tenant_IdempotencyKey'
)
    CREATE UNIQUE INDEX UX_SmsMessages_Tenant_IdempotencyKey
        ON dbo.SmsMessages(TenantId, IdempotencyKey)
        WHERE IdempotencyKey IS NOT NULL;
GO

IF COL_LENGTH(N'dbo.SmsMessages', N'SegmentCount') IS NULL
    ALTER TABLE dbo.SmsMessages ADD SegmentCount INT NULL;
GO
IF COL_LENGTH(N'dbo.SmsMessages', N'UnitPrice') IS NULL
    ALTER TABLE dbo.SmsMessages ADD UnitPrice DECIMAL(18,4) NULL;
GO
IF COL_LENGTH(N'dbo.SmsMessages', N'TotalCost') IS NULL
    ALTER TABLE dbo.SmsMessages ADD TotalCost DECIMAL(18,4) NULL;
GO
IF COL_LENGTH(N'dbo.SmsMessages', N'PricingVersion') IS NULL
    ALTER TABLE dbo.SmsMessages ADD PricingVersion NVARCHAR(50) NULL;
GO
IF COL_LENGTH(N'dbo.SmsMessages', N'BillingStatus') IS NULL
BEGIN
    ALTER TABLE dbo.SmsMessages ADD BillingStatus TINYINT NOT NULL
        CONSTRAINT DF_SmsMessages_BillingStatus DEFAULT(0) WITH VALUES;
END;
GO
IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.SmsMessages')
      AND name = N'CK_SmsMessages_BillingStatus'
)
    ALTER TABLE dbo.SmsMessages WITH CHECK
        ADD CONSTRAINT CK_SmsMessages_BillingStatus CHECK(BillingStatus BETWEEN 0 AND 4);
GO

-- BillingStatus: 0=NotCalculated, 1=Calculated, 2=Debited, 3=DebitFailed, 4=Exempt.
-- UnitPrice/TotalCost remain NULL until the billing feature is enabled.
