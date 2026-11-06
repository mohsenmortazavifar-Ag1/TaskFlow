import {getPool, sql} from "@/lib/db";
import type {TenantDatabaseRef} from "@/lib/tenant-db";

export type ReminderDefinition = {
  reminderScheduleId: number;
  reminderType: string;
  sourceEntity: string;
  sourceEntityId: number;
  ruleJson: string;
  timeZoneId: string;
  scheduleVersion: number;
  isEnabled: boolean;
  nextRunAtUtc: Date | null;
  updatedAt: Date;
};

export async function syncReminderProjection(
  tenant: TenantDatabaseRef,
  reminder: ReminderDefinition
): Promise<void> {
  const pool = await getPool();
  await pool.request()
    .input("TenantId", sql.BigInt, tenant.tenantId)
    .input("TenantDatabaseId", sql.BigInt, tenant.tenantDatabaseId)
    .input("TenantReminderScheduleId", sql.BigInt, reminder.reminderScheduleId)
    .input("ReminderType", sql.NVarChar(100), reminder.reminderType)
    .input("SourceEntity", sql.NVarChar(100), reminder.sourceEntity)
    .input("SourceEntityId", sql.BigInt, reminder.sourceEntityId)
    .input("RuleJson", sql.NVarChar(sql.MAX), reminder.ruleJson)
    .input("TimeZoneId", sql.NVarChar(100), reminder.timeZoneId)
    .input("ScheduleVersion", sql.Int, reminder.scheduleVersion)
    .input("IsEnabled", sql.Bit, reminder.isEnabled)
    .input("NextRunAtUtc", sql.DateTime2, reminder.nextRunAtUtc)
    .input("SourceUpdatedAt", sql.DateTime2, reminder.updatedAt)
    .query(`
      SET XACT_ABORT ON;
      BEGIN TRANSACTION;

      UPDATE dbo.ReminderSchedules WITH (UPDLOCK, SERIALIZABLE)
      SET TenantId = @TenantId,
          ReminderType = @ReminderType,
          SourceEntity = @SourceEntity,
          SourceEntityId = @SourceEntityId,
          RuleJson = @RuleJson,
          TimeZoneId = @TimeZoneId,
          ScheduleVersion = @ScheduleVersion,
          IsEnabled = @IsEnabled,
          NextRunAtUtc = @NextRunAtUtc,
          SourceUpdatedAt = @SourceUpdatedAt,
          UpdatedAt = SYSUTCDATETIME()
      WHERE TenantDatabaseId = @TenantDatabaseId
        AND TenantReminderScheduleId = @TenantReminderScheduleId;

      IF @@ROWCOUNT = 0
      BEGIN
        INSERT INTO dbo.ReminderSchedules
          (TenantId, ReminderType, SourceEntity, SourceEntityId, RuleJson,
           TimeZoneId, ScheduleVersion, IsEnabled, NextRunAtUtc,
           TenantDatabaseId, TenantReminderScheduleId, SourceUpdatedAt)
        VALUES
          (@TenantId, @ReminderType, @SourceEntity, @SourceEntityId, @RuleJson,
           @TimeZoneId, @ScheduleVersion, @IsEnabled, @NextRunAtUtc,
           @TenantDatabaseId, @TenantReminderScheduleId, @SourceUpdatedAt);
      END;

      COMMIT TRANSACTION;
    `);
}
