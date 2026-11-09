/*
  Seed the RepairCompleted follow-up reminder rule.
  Run this script in EACH Repair tenant operational database after:
    001_outbox_schema.sql
    002_reminder_schedules.sql
    003_reminder_event_rules.sql

  Default policy: one follow-up reminder 3 days after repair completion.
  Adjust TimeZoneId and MessageTemplate to the tenant's business policy before rollout.
  ProviderCode is NULL intentionally: the Worker resolves the provider from TenantSmsSettings.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.ReminderEventRules', N'U') IS NULL
    THROW 51000, 'dbo.ReminderEventRules does not exist. Apply 003_reminder_event_rules.sql first.', 1;

DECLARE @EventType nvarchar(100) = N'RepairCompleted';
DECLARE @ReminderType nvarchar(100) = N'RepairCompletedFollowUp';
DECLARE @SourceEntity nvarchar(100) = N'RepairOrder';
DECLARE @DelayMinutes int = 4320;
DECLARE @TimeZoneId nvarchar(100) = N'Asia/Tehran';
DECLARE @MobileTemplate nvarchar(100) = N'{{customerMobile}}';
DECLARE @MessageTemplate nvarchar(2000) =
    N'سلام {{customerName}} عزیز، سه روز از تکمیل تعمیر دستگاه با شماره رسید {{repairOrderNumber}} گذشته است. در صورت بروز مشکل یا نیاز به پیگیری، با ما تماس بگیرید.';

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @ExistingCount int;
    SELECT @ExistingCount = COUNT(*)
    FROM dbo.ReminderEventRules WITH (UPDLOCK, HOLDLOCK)
    WHERE EventType = @EventType
      AND ReminderType = @ReminderType
      AND SourceEntity = @SourceEntity;

    IF @ExistingCount > 1
        THROW 51001, 'Duplicate RepairCompletedFollowUp rules exist. Resolve duplicates before running this seed.', 1;

    IF @ExistingCount = 1
    BEGIN
        UPDATE dbo.ReminderEventRules
        SET DelayMinutes = @DelayMinutes,
            TimeZoneId = @TimeZoneId,
            MobileTemplate = @MobileTemplate,
            MessageTemplate = @MessageTemplate,
            ProviderCode = NULL,
            RecurrenceIntervalValue = NULL,
            RecurrenceIntervalUnit = NULL,
            LocalTime = NULL,
            IsEnabled = 1,
            UpdatedAt = SYSUTCDATETIME()
        WHERE EventType = @EventType
          AND ReminderType = @ReminderType
          AND SourceEntity = @SourceEntity;
    END
    ELSE
    BEGIN
        INSERT INTO dbo.ReminderEventRules
        (
            EventType, ReminderType, SourceEntity, DelayMinutes, TimeZoneId,
            MobileTemplate, MessageTemplate, ProviderCode,
            RecurrenceIntervalValue, RecurrenceIntervalUnit, LocalTime, IsEnabled
        )
        VALUES
        (
            @EventType, @ReminderType, @SourceEntity, @DelayMinutes, @TimeZoneId,
            @MobileTemplate, @MessageTemplate, NULL,
            NULL, NULL, NULL, 1
        );
    END;

    COMMIT TRANSACTION;

    SELECT ReminderEventRuleId, EventType, ReminderType, SourceEntity,
           DelayMinutes, TimeZoneId, MobileTemplate, MessageTemplate,
           ProviderCode, IsEnabled, UpdatedAt
    FROM dbo.ReminderEventRules
    WHERE EventType = @EventType
      AND ReminderType = @ReminderType
      AND SourceEntity = @SourceEntity;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
