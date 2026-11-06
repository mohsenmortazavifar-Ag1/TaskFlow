import {NextResponse} from "next/server";
import {z} from "zod";
import {hasValidApiKey} from "@/lib/auth";
import {sql} from "@/lib/db";
import {syncReminderProjection} from "@/lib/reminders";
import {TenantDatabaseError, withTenantDatabase} from "@/lib/tenant-db";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

const ruleSchema = z.object({
  mobile: z.string().trim().min(1).max(20),
  message: z.string().trim().min(1).max(2000),
  providerCode: z.string().trim().max(50).optional(),
  intervalValue: z.number().int().min(1).max(3650).optional(),
  intervalUnit: z.enum(["day", "month", "year"]).optional(),
  localTime: z.string().regex(/^(?:[01]\d|2[0-3]):[0-5]\d$/).optional()
}).strict().refine(v => (v.intervalValue === undefined) === (v.intervalUnit === undefined), {
  message: "intervalValue and intervalUnit must be supplied together."
});

const patchSchema = z.object({
  tenantId: z.number().int().positive(),
  tenantDatabaseId: z.number().int().positive().optional(),
  expectedScheduleVersion: z.number().int().positive(),
  reminderType: z.string().trim().min(1).max(100).optional(),
  sourceEntity: z.string().trim().min(1).max(100).optional(),
  sourceEntityId: z.number().int().positive().optional(),
  rule: ruleSchema.optional(),
  timeZoneId: z.string().trim().min(1).max(100).optional(),
  nextRunAtUtc: z.string().datetime({offset: true}).optional()
});

function errorResponse(error: unknown) {
  if (error instanceof TenantDatabaseError)
    return NextResponse.json({error: error.message}, {status: error.status});
  if (error instanceof Error && error.message === "REMINDER_NOT_FOUND")
    return NextResponse.json({error: "Reminder پیدا نشد."}, {status: 404});
  if (error instanceof Error && error.message === "REMINDER_VERSION_CONFLICT")
    return NextResponse.json({error: "Reminder توسط درخواست دیگری تغییر کرده است؛ اطلاعات را دوباره دریافت کنید."}, {status: 409});
  return NextResponse.json({error: "عملیات Reminder با خطا مواجه شد."}, {status: 500});
}

function parseId(value: string): string | null {
  return /^\d{1,19}$/.test(value) && BigInt(value) > 0n ? value : null;
}

export async function PATCH(req: Request, context: {params: Promise<{reminderId: string}>}) {
  if (!hasValidApiKey(req)) return NextResponse.json({error: "Unauthorized"}, {status: 401});
  const {reminderId: rawId} = await context.params;
  const reminderId = parseId(rawId);
  if (!reminderId) return NextResponse.json({error: "شناسه Reminder نامعتبر است."}, {status: 400});
  const body = await req.json().catch(() => null);
  const parsed = patchSchema.safeParse(body);
  if (!parsed.success)
    return NextResponse.json({error: "ورودی ویرایش Reminder نامعتبر است.", details: parsed.error.flatten()}, {status: 400});

  const b = parsed.data;
  try {
    const updated = await withTenantDatabase(b.tenantId, b.tenantDatabaseId, async (pool, tenant) => {
      const transaction = new sql.Transaction(pool);
      await transaction.begin(sql.ISOLATION_LEVEL.SERIALIZABLE);
      try {
        const currentResult = await new sql.Request(transaction)
          .input("ReminderScheduleId", sql.BigInt, reminderId)
          .query(`
            SELECT ReminderScheduleId, ReminderType, SourceEntity, SourceEntityId, RuleJson,
                   TimeZoneId, ScheduleVersion, IsEnabled, NextRunAtUtc
            FROM dbo.ReminderSchedules WITH (UPDLOCK, HOLDLOCK)
            WHERE ReminderScheduleId = @ReminderScheduleId;
          `);
        const current = currentResult.recordset[0];
        if (!current) throw new Error("REMINDER_NOT_FOUND");
        if (current.ScheduleVersion !== b.expectedScheduleVersion) throw new Error("REMINDER_VERSION_CONFLICT");

        const values = {
          reminderType: b.reminderType ?? current.ReminderType,
          sourceEntity: b.sourceEntity ?? current.SourceEntity,
          sourceEntityId: b.sourceEntityId ?? current.SourceEntityId,
          ruleJson: b.rule ? JSON.stringify(b.rule) : current.RuleJson,
          timeZoneId: b.timeZoneId ?? current.TimeZoneId,
          nextRunAtUtc: b.nextRunAtUtc ? new Date(b.nextRunAtUtc) : current.NextRunAtUtc,
          scheduleVersion: current.ScheduleVersion + 1
        };

        const result = await new sql.Request(transaction)
          .input("ReminderScheduleId", sql.BigInt, reminderId)
          .input("ReminderType", sql.NVarChar(100), values.reminderType)
          .input("SourceEntity", sql.NVarChar(100), values.sourceEntity)
          .input("SourceEntityId", sql.BigInt, values.sourceEntityId)
          .input("RuleJson", sql.NVarChar(sql.MAX), values.ruleJson)
          .input("TimeZoneId", sql.NVarChar(100), values.timeZoneId)
          .input("NextRunAtUtc", sql.DateTime2, values.nextRunAtUtc)
          .input("ScheduleVersion", sql.Int, values.scheduleVersion)
          .query(`
            UPDATE dbo.ReminderSchedules
            SET ReminderType = @ReminderType, SourceEntity = @SourceEntity,
                SourceEntityId = @SourceEntityId, RuleJson = @RuleJson,
                TimeZoneId = @TimeZoneId, NextRunAtUtc = @NextRunAtUtc,
                ScheduleVersion = @ScheduleVersion, UpdatedAt = SYSUTCDATETIME()
            OUTPUT INSERTED.ReminderScheduleId, INSERTED.ReminderType, INSERTED.SourceEntity,
                   INSERTED.SourceEntityId, INSERTED.RuleJson, INSERTED.TimeZoneId,
                   INSERTED.ScheduleVersion, INSERTED.IsEnabled, INSERTED.NextRunAtUtc,
                   INSERTED.UpdatedAt
            WHERE ReminderScheduleId = @ReminderScheduleId;
          `);
        await transaction.commit();
        const row = result.recordset[0];
        return {
          tenant,
          reminder: {
            reminderScheduleId: row.ReminderScheduleId,
            reminderType: row.ReminderType,
            sourceEntity: row.SourceEntity,
            sourceEntityId: row.SourceEntityId,
            ruleJson: row.RuleJson,
            timeZoneId: row.TimeZoneId,
            scheduleVersion: row.ScheduleVersion,
            isEnabled: row.IsEnabled,
            nextRunAtUtc: row.NextRunAtUtc,
            updatedAt: row.UpdatedAt
          }
        };
      } catch (error) {
        await transaction.rollback().catch(() => undefined);
        throw error;
      }
    });

    try {
      await syncReminderProjection(updated.tenant, updated.reminder);
      return NextResponse.json({data: updated.reminder});
    } catch {
      return NextResponse.json({
        data: updated.reminder,
        warning: "تغییر در دیتابیس Tenant ذخیره شد؛ همگام‌سازی مرکزی در چرخه بعدی Worker انجام می‌شود."
      }, {status: 202});
    }
  } catch (error) {
    return errorResponse(error);
  }
}

export async function DELETE(req: Request, context: {params: Promise<{reminderId: string}>}) {
  if (!hasValidApiKey(req)) return NextResponse.json({error: "Unauthorized"}, {status: 401});
  const {reminderId: rawId} = await context.params;
  const reminderId = parseId(rawId);
  if (!reminderId) return NextResponse.json({error: "شناسه Reminder نامعتبر است."}, {status: 400});
  const url = new URL(req.url);
  const parsed = z.object({
    tenantId: z.coerce.number().int().positive(),
    tenantDatabaseId: z.coerce.number().int().positive().optional(),
    expectedScheduleVersion: z.coerce.number().int().positive()
  }).safeParse({
    tenantId: url.searchParams.get("tenantId"),
    tenantDatabaseId: url.searchParams.get("tenantDatabaseId") ?? undefined,
    expectedScheduleVersion: url.searchParams.get("expectedScheduleVersion")
  });
  if (!parsed.success)
    return NextResponse.json({error: "tenantId و expectedScheduleVersion الزامی و باید معتبر باشند."}, {status: 400});

  const b = parsed.data;
  try {
    const cancelled = await withTenantDatabase(b.tenantId, b.tenantDatabaseId, async (pool, tenant) => {
      const transaction = new sql.Transaction(pool);
      await transaction.begin(sql.ISOLATION_LEVEL.SERIALIZABLE);
      try {
        const result = await new sql.Request(transaction)
          .input("ReminderScheduleId", sql.BigInt, reminderId)
          .query(`
            SELECT ReminderScheduleId, ReminderType, SourceEntity, SourceEntityId, RuleJson,
                   TimeZoneId, ScheduleVersion, IsEnabled
            FROM dbo.ReminderSchedules WITH (UPDLOCK, HOLDLOCK)
            WHERE ReminderScheduleId = @ReminderScheduleId;
          `);
        const current = result.recordset[0];
        if (!current) throw new Error("REMINDER_NOT_FOUND");
        if (current.ScheduleVersion !== b.expectedScheduleVersion) throw new Error("REMINDER_VERSION_CONFLICT");

        const update = await new sql.Request(transaction)
          .input("ReminderScheduleId", sql.BigInt, reminderId)
          .input("ScheduleVersion", sql.Int, current.ScheduleVersion + 1)
          .query(`
            UPDATE dbo.ReminderSchedules
            SET IsEnabled = 0, NextRunAtUtc = NULL,
                ScheduleVersion = @ScheduleVersion, UpdatedAt = SYSUTCDATETIME()
            OUTPUT INSERTED.ReminderScheduleId, INSERTED.ReminderType, INSERTED.SourceEntity,
                   INSERTED.SourceEntityId, INSERTED.RuleJson, INSERTED.TimeZoneId,
                   INSERTED.ScheduleVersion, INSERTED.IsEnabled, INSERTED.NextRunAtUtc,
                   INSERTED.UpdatedAt
            WHERE ReminderScheduleId = @ReminderScheduleId;
          `);
        await transaction.commit();
        const row = update.recordset[0];
        return {
          tenant,
          reminder: {
            reminderScheduleId: row.ReminderScheduleId,
            reminderType: row.ReminderType,
            sourceEntity: row.SourceEntity,
            sourceEntityId: row.SourceEntityId,
            ruleJson: row.RuleJson,
            timeZoneId: row.TimeZoneId,
            scheduleVersion: row.ScheduleVersion,
            isEnabled: row.IsEnabled,
            nextRunAtUtc: row.NextRunAtUtc,
            updatedAt: row.UpdatedAt
          }
        };
      } catch (error) {
        await transaction.rollback().catch(() => undefined);
        throw error;
      }
    });

    try {
      await syncReminderProjection(cancelled.tenant, cancelled.reminder);
      return NextResponse.json({data: cancelled.reminder, cancelled: true});
    } catch {
      return NextResponse.json({
        data: cancelled.reminder,
        cancelled: true,
        warning: "لغو در دیتابیس Tenant ذخیره شد؛ همگام‌سازی مرکزی در چرخه بعدی Worker انجام می‌شود."
      }, {status: 202});
    }
  } catch (error) {
    return errorResponse(error);
  }
}
