import {NextResponse} from "next/server";
import {z} from "zod";
import {hasValidApiKey} from "@/lib/auth";
import {sql} from "@/lib/db";
import {syncReminderProjection, type ReminderDefinition} from "@/lib/reminders";
import {TenantDatabaseError, withTenantDatabase} from "@/lib/tenant-db";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

const eventSchema = z.object({
  tenantId: z.number().int().positive(),
  tenantDatabaseId: z.number().int().positive().optional(),
  eventId: z.string().uuid(),
  eventType: z.string().trim().min(1).max(100).regex(/^[A-Za-z][A-Za-z0-9_.-]*$/),
  sourceEntity: z.string().trim().min(1).max(100),
  sourceEntityId: z.string().regex(/^[1-9][0-9]{0,18}$/),
  occurredAtUtc: z.string().datetime({offset: true}),
  data: z.record(z.string(), z.string().max(2000)).default({})
});

type EventRule = {
  ReminderEventRuleId: number;
  EventType: string;
  ReminderType: string;
  SourceEntity: string;
  DelayMinutes: number;
  TimeZoneId: string;
  MobileTemplate: string;
  MessageTemplate: string;
  ProviderCode: string | null;
  RecurrenceIntervalValue: number | null;
  RecurrenceIntervalUnit: "day" | "month" | "year" | null;
  LocalTime: string | null;
};

function renderTemplate(template: string, values: Record<string, string>): string {
  return template.replace(/{{\s*([A-Za-z][A-Za-z0-9_]*)\s*}}/g, (_match, key: string) => {
    if (!(key in values)) throw new Error(`Missing event template value: ${key}`);
    return values[key];
  });
}

function errorResponse(error: unknown) {
  if (error instanceof TenantDatabaseError)
    return NextResponse.json({error: error.message}, {status: error.status});
  if (error instanceof Error && error.message.startsWith("EVENT_"))
    return NextResponse.json({error: error.message}, {status: 400});
  return NextResponse.json({error: "پردازش رویداد Reminder با خطا مواجه شد."}, {status: 500});
}

export async function POST(req: Request) {
  if (!hasValidApiKey(req)) return NextResponse.json({error: "Unauthorized"}, {status: 401});

  const body = await req.json().catch(() => null);
  const parsed = eventSchema.safeParse(body);
  if (!parsed.success)
    return NextResponse.json(
      {error: "ساختار رویداد نامعتبر است.", details: parsed.error.flatten()},
      {status: 400}
    );

  const event = parsed.data;
  try {
    const outcome = await withTenantDatabase(
      event.tenantId,
      event.tenantDatabaseId,
      async (pool, tenant) => {
        const transaction = new sql.Transaction(pool);
        await transaction.begin(sql.ISOLATION_LEVEL.SERIALIZABLE);
        try {
          const existing = await new sql.Request(transaction)
            .input("EventId", sql.UniqueIdentifier, event.eventId)
            .query(`
              SELECT Status, CreatedReminderCount
              FROM dbo.ReminderEventInbox WITH (UPDLOCK, HOLDLOCK)
              WHERE EventId = @EventId;
            `);

          if (existing.recordset.length > 0) {
            await transaction.commit();
            return {
              tenant,
              duplicate: true,
              status: existing.recordset[0].Status,
              createdCount: existing.recordset[0].CreatedReminderCount,
              reminders: [] as ReminderDefinition[]
            };
          }

          await new sql.Request(transaction)
            .input("EventId", sql.UniqueIdentifier, event.eventId)
            .input("EventType", sql.NVarChar(100), event.eventType)
            .input("SourceEntity", sql.NVarChar(100), event.sourceEntity)
            .input("SourceEntityId", sql.BigInt, event.sourceEntityId)
            .input("OccurredAtUtc", sql.DateTime2, new Date(event.occurredAtUtc))
            .query(`
              INSERT INTO dbo.ReminderEventInbox
                (EventId, EventType, SourceEntity, SourceEntityId, OccurredAtUtc, Status)
              VALUES
                (@EventId, @EventType, @SourceEntity, @SourceEntityId, @OccurredAtUtc, 0);
            `);

          const rulesResult = await new sql.Request(transaction)
            .input("EventType", sql.NVarChar(100), event.eventType)
            .query<EventRule>(`
              SELECT ReminderEventRuleId, EventType, ReminderType, SourceEntity,
                     DelayMinutes, TimeZoneId, MobileTemplate, MessageTemplate,
                     ProviderCode, RecurrenceIntervalValue, RecurrenceIntervalUnit, LocalTime
              FROM dbo.ReminderEventRules WITH (UPDLOCK, HOLDLOCK)
              WHERE EventType = @EventType AND IsEnabled = 1
              ORDER BY ReminderEventRuleId;
            `);

          const values: Record<string, string> = {
            ...event.data,
            eventId: event.eventId,
            eventType: event.eventType,
            sourceEntity: event.sourceEntity,
            sourceEntityId: event.sourceEntityId,
            occurredAtUtc: event.occurredAtUtc
          };
          const reminders: ReminderDefinition[] = [];

          for (const rule of rulesResult.recordset) {
            const mobile = renderTemplate(rule.MobileTemplate, values).trim();
            const message = renderTemplate(rule.MessageTemplate, values).trim();
            if (!mobile || mobile.length > 20) throw new Error("EVENT_INVALID_RENDERED_MOBILE");
            if (!message || message.length > 2000) throw new Error("EVENT_INVALID_RENDERED_MESSAGE");

            const dueAt = new Date(new Date(event.occurredAtUtc).getTime() + rule.DelayMinutes * 60_000);
            const ruleJson = {
              mobile,
              message,
              ...(rule.ProviderCode ? {providerCode: rule.ProviderCode} : {}),
              ...(rule.RecurrenceIntervalValue && rule.RecurrenceIntervalUnit
                ? {intervalValue: rule.RecurrenceIntervalValue, intervalUnit: rule.RecurrenceIntervalUnit}
                : {}),
              ...(rule.LocalTime ? {localTime: rule.LocalTime} : {})
            };

            const inserted = await new sql.Request(transaction)
              .input("ReminderType", sql.NVarChar(100), rule.ReminderType)
              .input("SourceEntity", sql.NVarChar(100), rule.SourceEntity)
              .input("SourceEntityId", sql.BigInt, event.sourceEntityId)
              .input("RuleJson", sql.NVarChar(sql.MAX), JSON.stringify(ruleJson))
              .input("TimeZoneId", sql.NVarChar(100), rule.TimeZoneId)
              .input("NextRunAtUtc", sql.DateTime2, dueAt)
              .query(`
                INSERT INTO dbo.ReminderSchedules
                  (ReminderType, SourceEntity, SourceEntityId, RuleJson, TimeZoneId,
                   ScheduleVersion, IsEnabled, NextRunAtUtc, CreatedAt, UpdatedAt)
                OUTPUT INSERTED.ReminderScheduleId, INSERTED.ReminderType,
                       INSERTED.SourceEntity, INSERTED.SourceEntityId, INSERTED.RuleJson,
                       INSERTED.TimeZoneId, INSERTED.ScheduleVersion, INSERTED.IsEnabled,
                       INSERTED.NextRunAtUtc, INSERTED.UpdatedAt
                VALUES
                  (@ReminderType, @SourceEntity, @SourceEntityId, @RuleJson, @TimeZoneId,
                   1, 1, @NextRunAtUtc, SYSUTCDATETIME(), SYSUTCDATETIME());
              `);

            const row = inserted.recordset[0];
            reminders.push({
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
            });
          }

          const status = reminders.length === 0 ? 2 : 1;
          await new sql.Request(transaction)
            .input("EventId", sql.UniqueIdentifier, event.eventId)
            .input("Status", sql.TinyInt, status)
            .input("CreatedReminderCount", sql.Int, reminders.length)
            .query(`
              UPDATE dbo.ReminderEventInbox
              SET Status = @Status,
                  CreatedReminderCount = @CreatedReminderCount,
                  ProcessedAtUtc = SYSUTCDATETIME()
              WHERE EventId = @EventId;
            `);

          await transaction.commit();
          return {tenant, duplicate: false, status, createdCount: reminders.length, reminders};
        } catch (error) {
          await transaction.rollback().catch(() => undefined);
          throw error;
        }
      }
    );

    if (outcome.duplicate) {
      return NextResponse.json({
        accepted: true,
        duplicate: true,
        createdReminderCount: outcome.createdCount
      });
    }

    let syncPending = false;
    for (const reminder of outcome.reminders) {
      try {
        await syncReminderProjection(outcome.tenant, reminder);
      } catch {
        syncPending = true;
      }
    }

    return NextResponse.json({
      accepted: true,
      eventId: event.eventId,
      createdReminderCount: outcome.createdCount,
      synchronizationPending: syncPending
    }, {status: syncPending ? 202 : 201});
  } catch (error) {
    return errorResponse(error);
  }
}
