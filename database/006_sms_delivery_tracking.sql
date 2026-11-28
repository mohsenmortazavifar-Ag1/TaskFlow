IF DB_ID() <> DB_ID(N'TaskFlow')
    THROW 50020, 'Run this migration in the TaskFlow database.', 1;
GO

/*
    SMS submission and delivery tracking.
    Existing Status (0..6) is intentionally not reinterpreted; the new columns
    provide an explicit provider submission/delivery state machine.
*/
IF COL_LENGTH('dbo.SmsMessages', 'SubmissionState') IS NULL
BEGIN
    ALTER TABLE dbo.SmsMessages
    ADD SubmissionState TINYINT NOT NULL
        CONSTRAINT DF_SmsMessages_SubmissionState DEFAULT (0) WITH VALUES;
END;
GO

IF COL_LENGTH('dbo.SmsMessages', 'SubmissionStartedAt') IS NULL
    ALTER TABLE dbo.SmsMessages ADD SubmissionStartedAt DATETIME2(3) NULL;
GO

IF COL_LENGTH('dbo.SmsMessages', 'DeliveryState') IS NULL
BEGIN
    ALTER TABLE dbo.SmsMessages
    ADD DeliveryState TINYINT NOT NULL
        CONSTRAINT DF_SmsMessages_DeliveryState DEFAULT (0) WITH VALUES;
END;
GO

IF COL_LENGTH('dbo.SmsMessages', 'DeliveryCheckCount') IS NULL
BEGIN
    ALTER TABLE dbo.SmsMessages
    ADD DeliveryCheckCount TINYINT NOT NULL
        CONSTRAINT DF_SmsMessages_DeliveryCheckCount DEFAULT (0) WITH VALUES;
END;
GO

IF COL_LENGTH('dbo.SmsMessages', 'LastDeliveryCheckAt') IS NULL
    ALTER TABLE dbo.SmsMessages ADD LastDeliveryCheckAt DATETIME2(3) NULL;
GO

IF COL_LENGTH('dbo.SmsMessages', 'NextDeliveryCheckAt') IS NULL
    ALTER TABLE dbo.SmsMessages ADD NextDeliveryCheckAt DATETIME2(3) NULL;
GO

IF COL_LENGTH('dbo.SmsMessages', 'ProviderDeliveryStatus') IS NULL
    ALTER TABLE dbo.SmsMessages ADD ProviderDeliveryStatus INT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SmsMessages_SubmissionState')
    ALTER TABLE dbo.SmsMessages WITH CHECK
    ADD CONSTRAINT CK_SmsMessages_SubmissionState CHECK (SubmissionState BETWEEN 0 AND 4);
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_SmsMessages_DeliveryState')
    ALTER TABLE dbo.SmsMessages WITH CHECK
    ADD CONSTRAINT CK_SmsMessages_DeliveryState CHECK (DeliveryState BETWEEN 0 AND 5);
GO

/*
    Conservative migration of legacy rows:
    - Already accepted rows become Accepted; those with ReqID are eligible for delivery checks.
    - The previously observed malformed-success response is ambiguous: the provider may
      already have accepted/sent the SMS. Mark it NeedsReview so it can never be resent.
    - Other unaccepted legacy rows remain Pending; known local configuration failures
      did not reach the provider and may be deliberately reprocessed by an operator.
*/
UPDATE dbo.SmsMessages
SET SubmissionState = 2,
    DeliveryState = CASE WHEN ProviderMessageId IS NULL THEN 5 ELSE 1 END,
    NextDeliveryCheckAt = CASE
        WHEN ProviderMessageId IS NOT NULL
            THEN DATEADD(SECOND, 120, COALESCE(AcceptedAt, CreatedAt))
        ELSE NULL
    END
WHERE AcceptedAt IS NOT NULL
  AND SubmissionState = 0;

UPDATE m
SET SubmissionState = 4,
    DeliveryState = 5,
    NextDeliveryCheckAt = NULL,
    LastError = COALESCE(m.LastError,
        N'Legacy SendSms attempt returned provider success but the response parser failed. Provider outcome is ambiguous; manual reconciliation is required.')
FROM dbo.SmsMessages AS m
INNER JOIN dbo.Jobs AS j ON j.JobId = m.JobId
WHERE m.SubmissionState = 0
  AND m.AcceptedAt IS NULL
  AND m.ProviderMessageId IS NULL
  AND j.LastError LIKE N'%ParsGreen returned R_Success=true without the expected Data object%';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.SmsMessages')
                 AND name = N'IX_SmsMessages_DeliveryDue')
BEGIN
    CREATE INDEX IX_SmsMessages_DeliveryDue
        ON dbo.SmsMessages(DeliveryState, NextDeliveryCheckAt, SmsMessageId)
        INCLUDE (TenantId, ProviderMessageId, DeliveryCheckCount, SubmissionState)
        WHERE DeliveryState = 1;
END;
GO
