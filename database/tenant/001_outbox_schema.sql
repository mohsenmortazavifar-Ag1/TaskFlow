/*
  Run once in every tenant operational database before enabling Outbox Dispatcher.
  Status: 0=Pending, 1=Processing, 2=Published, 3=Dead.
*/
IF OBJECT_ID(N'dbo.OutboxMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutboxMessages
    (
        OutboxId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_OutboxMessages PRIMARY KEY,
        EventId UNIQUEIDENTIFIER NOT NULL,
        TenantId BIGINT NOT NULL,
        EventType NVARCHAR(100) NOT NULL,
        QueueName NVARCHAR(50) NOT NULL,
        JobType NVARCHAR(100) NOT NULL,
        Payload NVARCHAR(MAX) NOT NULL,
        Priority TINYINT NOT NULL CONSTRAINT DF_OutboxMessages_Priority DEFAULT (10),
        Status TINYINT NOT NULL CONSTRAINT DF_OutboxMessages_Status DEFAULT (0),
        AttemptCount INT NOT NULL CONSTRAINT DF_OutboxMessages_AttemptCount DEFAULT (0),
        MaxAttempts INT NOT NULL CONSTRAINT DF_OutboxMessages_MaxAttempts DEFAULT (10),
        NextAttemptAt DATETIME2(3) NULL,
        LockedBy NVARCHAR(150) NULL,
        LockedUntil DATETIME2(3) NULL,
        CreatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_OutboxMessages_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ProcessedAt DATETIME2(3) NULL,
        LastError NVARCHAR(2000) NULL,
        CONSTRAINT UQ_OutboxMessages_EventId UNIQUE (EventId),
        CONSTRAINT CK_OutboxMessages_Status CHECK (Status BETWEEN 0 AND 3),
        CONSTRAINT CK_OutboxMessages_Attempts CHECK (AttemptCount >= 0 AND MaxAttempts BETWEEN 1 AND 100),
        CONSTRAINT CK_OutboxMessages_Payload CHECK (ISJSON(Payload) = 1)
    );

    CREATE INDEX IX_OutboxMessages_Dispatch
        ON dbo.OutboxMessages(Status, NextAttemptAt, Priority DESC, CreatedAt, OutboxId)
        INCLUDE(EventId, TenantId, EventType, QueueName, JobType, AttemptCount, MaxAttempts, LockedUntil);
END;
GO

/*
Example SMS event. The operation that changes business data and this insert
must be committed in the SAME tenant-database transaction.

INSERT dbo.OutboxMessages
    (EventId, TenantId, EventType, QueueName, JobType, Payload, Priority)
VALUES
    (NEWID(), 125, N'SmsRequested', N'sms', N'SendSms',
     N'{"mobile":"09120000000","message":"Example message","providerCode":"default"}', 10);
*/
