import {NextResponse} from "next/server";
import {z} from "zod";
import {hasValidApiKey} from "@/lib/auth";
import {sql} from "@/lib/db";
import {TenantDatabaseError, withTenantDatabase} from "@/lib/tenant-db";
import {acquireTenantWorkerLock} from "@/lib/tenant-worker-lock";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

const ruleSchema = z.object({
  mobile: z.string().trim().min(1).max(20),
  message: z.string().trim().min(1).max(2000),
  providerCode: z.string().trim().max(50).optional(),
  intervalValue: z.number().int().min(1).max(3650).optional(),
  intervalUnit: z.enum(["day", "month", "year"]).optional(),
  maxOccurrences: z.number().int().min(1).max(1000).optional(),
  localTime: z.string().regex(/^(?:[01]\d|2[0-3]):[0-5]\d$/).optional()
}).strict().refine(v => (v.intervalValue === undefined) === (v.intervalUnit === undefined), {
  message: "intervalValue and intervalUnit must be supplied together."
}).refine(v => v.intervalValue === undefined || v.maxOccurrences !== undefined, {
  message: "maxOccurrences is required when recurrence is enabled."
}).refine(v => v.intervalValue !== undefined || v.maxOccurrences === undefined, {
  message: "maxOccurrences can only be supplied with a recurrence interval."
});

const createSchema = z.object({
  tenantId: z.number().int().positive(),
  tenantDatabaseId: z.number().int().positive().optional(),
  reminderType: z.string().trim().min(1).max(100),
  sourceEntity: z.string().trim().min(1).max(100),
  sourceEntityId: z.number().int().positive(),
  rule: ruleSchema,
  timeZoneId: z.string().trim().min(1).max(100),
  nextRunAtUtc: z.string().datetime({offset: true})
});

function errorResponse(error: unknown) {
  if (error instanceof TenantDatabaseError)
    return NextResponse.json({error: error.message}, {status: error.status});
  if (error instanceof Error && error.message.includes("TENANT_WORKER_LOCK_TIMEOUT"))
    return NextResponse.json({error: "این Tenant در حال پردازش است؛ چند لحظه دیگر دوباره تلاش کنید."}, {status: 409});
  return NextResponse.json({error: "عملیات Reminder با خطا مواجه شد."}, {status: 500});
}

export async function GET(req: Request) {
  if (!hasValidApiKey(req)) return NextResponse.json({error: "Unauthorized"}, {status: 401});
  const url = new URL(req.url);
  const tenantId = Number(url.searchParams.get("tenantId"));
  const tenantDatabaseIdRaw = url.searchParams.get("tenantDatabaseId");
  const tenantDatabaseId = tenantDatabaseIdRaw ? Number(tenantDatabaseIdRaw) : undefined;
  const parsed = z.object({
    tenantId: z.number().int().positive(),
    tenantDatabaseId: z.number().int().positive().optional()
  }).safeParse({tenantId, tenantDatabaseId});
  if (!parsed.success) return NextResponse.json({error: "tenantId یا tenantDatabaseId نامعتبر است."}, {status: 400});

  try {
    const data = await withTenantDatabase(parsed.data.tenantId, parsed.data.tenantDatabaseId, async pool => {
      const result = await pool.request()
        .input("take", sql.Int, Math.min(200, Math.max(1, Number(url.searchParams.get("take") ?? 50))))
        .query(`
          SELECT TOP (@take) ReminderScheduleId, ReminderType, SourceEntity, SourceEntityId,
                 RuleJson, TimeZoneId, ScheduleVersion, IsEnabled, NextRunAtUtc, CreatedAt, UpdatedAt
          FROM notification.ReminderSchedules
          ORDER BY UpdatedAt DESC, ReminderScheduleId DESC;
        `);
      return result.recordset;
    });
    return NextResponse.json({data});
  } catch (error) {
    return errorResponse(error);
  }
}

export async function POST(req: Request) {
  if (!hasValidApiKey(req)) return NextResponse.json({error: "Unauthorized"}, {status: 401});
  const body = await req.json().catch(() => null);
  const parsed = createSchema.safeParse(body);
  if (!parsed.success)
    return NextResponse.json({error: "ورودی ایجاد Reminder نامعتبر است.", details: parsed.error.flatten()}, {status: 400});

  const b = parsed.data;
  try {
    const created = await withTenantDatabase(b.tenantId, b.tenantDatabaseId, async (pool, tenant) => {
      const transaction = new sql.Transaction(pool);
      await transaction.begin(sql.ISOLATION_LEVEL.SERIALIZABLE);
      try {
        await acquireTenantWorkerLock(transaction, tenant.tenantDatabaseId);
        const result = await new sql.Request(transaction)
          .input("ReminderType", sql.NVarChar(100), b.reminderType)
          .input("SourceEntity", sql.NVarChar(100), b.sourceEntity)
          .input("SourceEntityId", sql.BigInt, b.sourceEntityId)
          .input("RuleJson", sql.NVarChar(sql.MAX), JSON.stringify(b.rule))
          .input("TimeZoneId", sql.NVarChar(100), b.timeZoneId)
          .input("NextRunAtUtc", sql.DateTime2, new Date(b.nextRunAtUtc))
          .query(`
            INSERT INTO notification.ReminderSchedules
              (ReminderType, SourceEntity, SourceEntityId, RuleJson, TimeZoneId,
               ScheduleVersion, IsEnabled, NextRunAtUtc, CreatedAt, UpdatedAt)
            OUTPUT INSERTED.ReminderScheduleId, INSERTED.ReminderType, INSERTED.SourceEntity,
                   INSERTED.SourceEntityId, INSERTED.RuleJson, INSERTED.TimeZoneId,
                   INSERTED.ScheduleVersion, INSERTED.IsEnabled, INSERTED.NextRunAtUtc,
                   INSERTED.UpdatedAt
            VALUES
              (@ReminderType, @SourceEntity, @SourceEntityId, @RuleJson, @TimeZoneId,
               1, 1, @NextRunAtUtc, SYSUTCDATETIME(), SYSUTCDATETIME());
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

    return NextResponse.json({data: created.reminder}, {status: 201});
  } catch (error) {
    return errorResponse(error);
  }
}
