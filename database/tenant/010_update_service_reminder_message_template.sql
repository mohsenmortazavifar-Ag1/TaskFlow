/*
  Updates only the RepairCompleted reminder message.
  Run in EACH tenant operational database after the RepairCompleted rule exists.
  This script intentionally does not change delay, recurrence, timezone, provider, or enabled state.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'notification.ReminderEventRules', N'U') IS NULL
    THROW 51000, 'notification.ReminderEventRules does not exist.', 1;

DECLARE @MessageTemplate nvarchar(2000) =
    N'سلام {{customerName}} عزیز، زمان سرویس دوره‌ای دستگاه شما فرا رسیده است. دستگاه: {{deviceType}} {{brand}} {{model}}. تعمیرگاه {{branchName}} - تماس: {{branchPhone}}';

DECLARE @RuleCount int;
SELECT @RuleCount = COUNT(*)
FROM notification.ReminderEventRules
WHERE EventType = N'RepairCompleted'
  AND ReminderType = N'RepairCompletedFollowUp'
  AND SourceEntity = N'RepairOrder';

IF @RuleCount <> 1
    THROW 51001, 'Expected exactly one RepairCompletedFollowUp rule. No changes were made.', 1;

UPDATE notification.ReminderEventRules
SET MessageTemplate = @MessageTemplate,
    UpdatedAt = SYSUTCDATETIME()
WHERE EventType = N'RepairCompleted'
  AND ReminderType = N'RepairCompletedFollowUp'
  AND SourceEntity = N'RepairOrder';

SELECT ReminderEventRuleId, EventType, ReminderType, MessageTemplate, IsEnabled, UpdatedAt
FROM notification.ReminderEventRules
WHERE EventType = N'RepairCompleted'
  AND ReminderType = N'RepairCompletedFollowUp'
  AND SourceEntity = N'RepairOrder';
