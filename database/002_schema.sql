IF DB_ID()<>DB_ID(N'TaskFlow') THROW 50001,'Run in TaskFlow database.',1;
GO
IF OBJECT_ID('dbo.JobQueues','U') IS NULL CREATE TABLE dbo.JobQueues(QueueId int IDENTITY PRIMARY KEY,QueueName nvarchar(50) NOT NULL UNIQUE,DisplayName nvarchar(100) NOT NULL,IsEnabled bit NOT NULL DEFAULT(1),MaxConcurrency smallint NOT NULL DEFAULT(1),MaxAttempts tinyint NOT NULL DEFAULT(5),VisibilityTimeoutSeconds int NOT NULL DEFAULT(60),PollingIntervalSeconds int NOT NULL DEFAULT(3),SortOrder smallint NOT NULL DEFAULT(0),CreatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),CONSTRAINT CK_JobQueues_Concurrency CHECK(MaxConcurrency BETWEEN 1 AND 100));
GO
IF OBJECT_ID('dbo.Jobs','U') IS NULL BEGIN
 CREATE TABLE dbo.Jobs(JobId bigint IDENTITY PRIMARY KEY,TenantId bigint NULL,QueueName nvarchar(50) NOT NULL,JobType nvarchar(100) NOT NULL,Payload nvarchar(max) NOT NULL,Status tinyint NOT NULL DEFAULT(0),Priority tinyint NOT NULL DEFAULT(10),ScheduledAt datetime2(3) NOT NULL,AttemptCount tinyint NOT NULL DEFAULT(0),MaxAttempts tinyint NOT NULL DEFAULT(5),NextAttemptAt datetime2(3) NULL,LockedBy nvarchar(150) NULL,LockedUntil datetime2(3) NULL,IdempotencyKey nvarchar(200) NULL,CreatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),StartedAt datetime2(3) NULL,CompletedAt datetime2(3) NULL,LastError nvarchar(2000) NULL,RowVersion rowversion NOT NULL,CONSTRAINT FK_Jobs_Queue FOREIGN KEY(QueueName) REFERENCES dbo.JobQueues(QueueName),CONSTRAINT CK_Jobs_Status CHECK(Status BETWEEN 0 AND 5),CONSTRAINT CK_Jobs_Payload CHECK(ISJSON(Payload)=1),CONSTRAINT CK_Jobs_Attempts CHECK(MaxAttempts BETWEEN 1 AND 20));
 CREATE INDEX IX_Jobs_Claim ON dbo.Jobs(QueueName,Status,NextAttemptAt,Priority DESC,CreatedAt) INCLUDE(TenantId,JobType,AttemptCount,MaxAttempts,LockedUntil) WHERE Status IN(0,3);
 CREATE INDEX IX_Jobs_Tenant_Created ON dbo.Jobs(TenantId,CreatedAt DESC);
 CREATE UNIQUE INDEX UX_Jobs_IdempotencyKey ON dbo.Jobs(TenantId,IdempotencyKey) WHERE IdempotencyKey IS NOT NULL;
END;
GO
IF OBJECT_ID('dbo.JobAttempts','U') IS NULL BEGIN
 CREATE TABLE dbo.JobAttempts(JobAttemptId bigint IDENTITY PRIMARY KEY,JobId bigint NOT NULL,AttemptNumber tinyint NOT NULL,WorkerId nvarchar(150) NOT NULL,StartedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),FinishedAt datetime2(3) NULL,Outcome tinyint NULL,ErrorMessage nvarchar(2000) NULL,DurationMs int NULL,CONSTRAINT FK_JobAttempts_Jobs FOREIGN KEY(JobId) REFERENCES dbo.Jobs(JobId),CONSTRAINT UQ_JobAttempts_Job_Attempt UNIQUE(JobId,AttemptNumber));
 CREATE INDEX IX_JobAttempts_Job ON dbo.JobAttempts(JobId,AttemptNumber DESC);
END;
GO
IF OBJECT_ID('dbo.WorkerInstances','U') IS NULL BEGIN
 CREATE TABLE dbo.WorkerInstances(WorkerId nvarchar(150) PRIMARY KEY,HostName nvarchar(150) NOT NULL,ProcessId int NULL,Version nvarchar(30) NULL,Status tinyint NOT NULL DEFAULT(1),StartedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),LastHeartbeatAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),StoppedAt datetime2(3) NULL,Metadata nvarchar(max) NULL,CONSTRAINT CK_Worker_Metadata CHECK(Metadata IS NULL OR ISJSON(Metadata)=1));
 CREATE INDEX IX_WorkerInstances_Heartbeat ON dbo.WorkerInstances(Status,LastHeartbeatAt);
END;
GO
IF OBJECT_ID('dbo.TenantQueueLimits','U') IS NULL CREATE TABLE dbo.TenantQueueLimits(TenantId bigint NOT NULL,QueueName nvarchar(50) NOT NULL,IsEnabled bit NOT NULL DEFAULT(1),MaxConcurrent smallint NOT NULL DEFAULT(2),MaxPerMinute int NULL,DailyLimit int NULL,Priority tinyint NOT NULL DEFAULT(10),CreatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),UpdatedAt datetime2(3) NULL,CONSTRAINT PK_TenantQueueLimits PRIMARY KEY(TenantId,QueueName),CONSTRAINT FK_TenantQueueLimits_Queue FOREIGN KEY(QueueName) REFERENCES dbo.JobQueues(QueueName));
GO
IF OBJECT_ID('dbo.SmsMessages','U') IS NULL BEGIN
 CREATE TABLE dbo.SmsMessages(SmsMessageId bigint IDENTITY PRIMARY KEY,TenantId bigint NOT NULL,JobId bigint NULL,Mobile nvarchar(20) NOT NULL,Message nvarchar(2000) NOT NULL,ProviderCode nvarchar(50) NULL,ProviderMessageId nvarchar(150) NULL,Status tinyint NOT NULL DEFAULT(0),CreatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),AcceptedAt datetime2(3) NULL,DeliveredAt datetime2(3) NULL,LastError nvarchar(1000) NULL,CONSTRAINT FK_SmsMessages_Job FOREIGN KEY(JobId) REFERENCES dbo.Jobs(JobId),CONSTRAINT CK_SmsMessages_Status CHECK(Status BETWEEN 0 AND 6));
 CREATE INDEX IX_SmsMessages_Tenant_Created ON dbo.SmsMessages(TenantId,CreatedAt DESC);
 CREATE INDEX IX_SmsMessages_Status ON dbo.SmsMessages(Status,CreatedAt);
END;
GO
IF OBJECT_ID('dbo.SmsLogs','U') IS NULL BEGIN
 CREATE TABLE dbo.SmsLogs(SmsLogId bigint IDENTITY PRIMARY KEY,SmsMessageId bigint NOT NULL,ProviderCode nvarchar(50) NULL,ProviderMessageId nvarchar(150) NULL,EventType nvarchar(50) NOT NULL,Description nvarchar(1000) NULL,CreatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),CONSTRAINT FK_SmsLogs_SmsMessages FOREIGN KEY(SmsMessageId) REFERENCES dbo.SmsMessages(SmsMessageId));
 CREATE INDEX IX_SmsLogs_Message_Created ON dbo.SmsLogs(SmsMessageId,CreatedAt DESC);
END;
GO
IF OBJECT_ID('dbo.TenantSmsSettings','U') IS NULL CREATE TABLE dbo.TenantSmsSettings(TenantId bigint PRIMARY KEY,IsEnabled bit NOT NULL DEFAULT(1),ProviderCode nvarchar(50) NOT NULL,SenderNumber nvarchar(30) NULL,MaxPerMinute int NULL,DailyLimit int NULL,UpdatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()));
GO
IF OBJECT_ID('dbo.ReminderSchedules','U') IS NULL BEGIN
 CREATE TABLE dbo.ReminderSchedules(ReminderScheduleId bigint IDENTITY PRIMARY KEY,TenantId bigint NOT NULL,ReminderType nvarchar(100) NOT NULL,SourceEntity nvarchar(100) NOT NULL,SourceEntityId bigint NOT NULL,RuleJson nvarchar(max) NOT NULL,TimeZoneId nvarchar(100) NOT NULL,ScheduleVersion int NOT NULL DEFAULT(1),IsEnabled bit NOT NULL DEFAULT(1),NextRunAtUtc datetime2(3) NULL,CreatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),UpdatedAt datetime2(3) NULL,CONSTRAINT CK_Reminder_RuleJson CHECK(ISJSON(RuleJson)=1));
 CREATE INDEX IX_ReminderSchedules_Due ON dbo.ReminderSchedules(IsEnabled,NextRunAtUtc) INCLUDE(TenantId,ScheduleVersion);
END;
GO
IF OBJECT_ID('dbo.ReminderOccurrences','U') IS NULL BEGIN
 CREATE TABLE dbo.ReminderOccurrences(ReminderOccurrenceId bigint IDENTITY PRIMARY KEY,ReminderScheduleId bigint NOT NULL,ScheduleVersion int NOT NULL,DueAtUtc datetime2(3) NOT NULL,Status tinyint NOT NULL DEFAULT(0),JobId bigint NULL,CreatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),ExecutedAt datetime2(3) NULL,CONSTRAINT FK_ReminderOccurrences_Schedule FOREIGN KEY(ReminderScheduleId) REFERENCES dbo.ReminderSchedules(ReminderScheduleId),CONSTRAINT FK_ReminderOccurrences_Job FOREIGN KEY(JobId) REFERENCES dbo.Jobs(JobId),CONSTRAINT UQ_ReminderOccurrence_Due UNIQUE(ReminderScheduleId,ScheduleVersion,DueAtUtc));
 CREATE INDEX IX_ReminderOccurrences_Status_Due ON dbo.ReminderOccurrences(Status,DueAtUtc);
END;
GO
IF SCHEMA_ID('ops') IS NULL EXEC('CREATE SCHEMA ops');
GO
IF OBJECT_ID('ops.WorkerEvents','U') IS NULL BEGIN
 CREATE TABLE ops.WorkerEvents(WorkerEventId bigint IDENTITY PRIMARY KEY,WorkerId nvarchar(150) NULL,EventType nvarchar(80) NOT NULL,Severity tinyint NOT NULL DEFAULT(1),Message nvarchar(2000) NOT NULL,Details nvarchar(max) NULL,CreatedAt datetime2(3) NOT NULL DEFAULT(SYSUTCDATETIME()),CONSTRAINT CK_WorkerEvents_Details CHECK(Details IS NULL OR ISJSON(Details)=1));
 CREATE INDEX IX_WorkerEvents_Created ON ops.WorkerEvents(CreatedAt DESC);
END;
GO
