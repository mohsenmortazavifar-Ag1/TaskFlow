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
  tenantDatabaseId: z.number().int().positive(),
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
  CancelEventType: string | null;
  MaxOccurrences: number | null;
};

function renderTemplate(template: string, values: Record<string, string>): string {
  return template.replace(/{{\s*([A-Za-z][A-Za-z0-9_]*)\s*}}/g, (_match, key: string) => {
    if (!(key in values)) throw new Error(`EVENT_MISSING_TEMPLATE_VALUE:${key}`);
    return values[key];
  });
}


function calculateInitialDueAt(
  occurredAtUtc: string,
  delayMinutes: number,
  timeZoneId: string,
  localTime: string | null
): Date {
  const delayed = new Date(new Date(occurredAtUtc).getTime() + delayMinutes * 60_000);
  if (!Number.isFinite(delayed.getTime())) throw new Error("EVENT_INVALID_OCCURRED_AT");
  if (!localTime) return delayed;
  if (!/^(?:[01]\\d|2[0-3]):[0-5]\\d$/.test(localTime))
    throw new Error("EVENT_INVALID_LOCAL_TIME");

  let formatter: Intl.DateTimeFormat;
  try {
    formatter = new Intl.DateTimeFormat("en-CA", {
      timeZone: timeZoneId, year: "numeric", month: "2-digit", day: "2-digit",
      hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23"
    });
    formatter.format(delayed);
  } catch {
    throw new Error("EVENT_INVALID_TIME_ZONE");
  }

  const parts = formatter.formatToParts(delayed);
  const part = (type: Intl.DateTimeFormatPartTypes) => parts.find(x => x.type === type)?.value ?? "";
  const year = Number(part("year"));
  const month = Number(part("month"));
  const day = Number(part("day"));
  const [hour, minute] = localTime.split(":").map(Number);
  let targetUtcLike = Date.UTC(year, month - 1, day, hour, minute, 0);
  const resolveLocalTime = () => {
    let candidate = targetUtcLike;
    for (let i = 0; i < 4; i++) {
      const probe = formatter.formatToParts(new Date(candidate));
      const value = (type: Intl.DateTimeFormatPartTypes) => Number(probe.find(x => x.type === type)?.value ?? 0);
      const represented = Date.UTC(value("year"), value("month") - 1, value("day"),
        value("hour"), value("minute"), value("second"));
      candidate += targetUtcLike - represented;
    }
    return new Date(candidate);
  };

  let resolved = resolveLocalTime();
  // The requested local clock time must not move the reminder before its delay has elapsed.
  if (resolved.getTime() < delayed.getTime()) {
    targetUtcLike += 24 * 60 * 60 * 1000;
    resolved = resolveLocalTime();
  }

  const check = formatter.formatToParts(resolved);
  const get = (type: Intl.DateTimeFormatPartTypes) => check.find(x => x.type === type)?.value;
  const expectedDate = new Date(targetUtcLike);
  if (Number(get("year")) !== expectedDate.getUTCFullYear() ||
      Number(get("month")) !== expectedDate.getUTCMonth() + 1 ||
      Number(get("day")) !== expectedDate.getUTCDate() ||
      Number(get("hour")) !== hour || Number(get("minute")) !== minute) {
    throw new Error("EVENT_LOCAL_TIME_INVALID_FOR_TIME_ZONE");
  }
  return resolved;
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
            const existingSchedules = await new sql.Request(transaction)
              .input("SourceEntity", sql.NVarChar(100), event.sourceEntity)
              .input("SourceEntityId", sql.BigInt, event.sourceEntityId)
              .query(`
                SELECT ReminderScheduleId, ReminderType, SourceEntity, SourceEntityId,
                       RuleJson, TimeZoneId, ScheduleVersion, IsEnabled, NextRunAtUtc, UpdatedAt
                FROM dbo.ReminderSchedules
                WHERE SourceEntity = @SourceEntity AND SourceEntityId = @SourceEntityId;
              `);
            const reminders: ReminderDefinition[] = existingSchedules.recordset.map(row => ({
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
            }));
            await transaction.commit();
            return {
              tenant,
              duplicate: true,
              status: existing.recordset[0].Status,
              createdCount: existing.recordset[0].CreatedReminderCount,
              reminders
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

          const reminders: ReminderDefinition[] = [];
          let createdReminderCount = 0;

          const cancellationRules = await new sql.Request(transaction)
            .input("CancelEventType", sql.NVarChar(100), event.eventType)
            .input("SourceEntity", sql.NVarChar(100), event.sourceEntity)
            .query<EventRule>(`
              SELECT ReminderEventRuleId, EventType, ReminderType, SourceEntity,
                     DelayMinutes, TimeZoneId, MobileTemplate, MessageTemplate,
                     ProviderCode, RecurrenceIntervalValue, RecurrenceIntervalUnit, LocalTime,
                     CancelEventType, MaxOccurrences
              FROM dbo.ReminderEventRules WITH (UPDLOCK, HOLDLOCK)
              WHERE EXISTS (
                  SELECT 1
                  FROM STRING_SPLIT(CancelEventType, N',') AS cancelType
                  WHERE LTRIM(RTRIM(cancelType.value)) = @CancelEventType
                )
                AND SourceEntity = @SourceEntity
                AND IsEnabled = 1
              ORDER BY ReminderEventRuleId;
            `);

          for (const cancelRule of cancellationRules.recordset) {
            const cancelled = await new sql.Request(transaction)
              .input("SourceEntity", sql.NVarChar(100), event.sourceEntity)
              .input("SourceEntityId", sql.BigInt, event.sourceEntityId)
              .input("ReminderType", sql.NVarChar(100), cancelRule.ReminderType)
              .input("ReminderEventRuleId", sql.BigInt, cancelRule.ReminderEventRuleId)
              .query(`
                UPDATE dbo.ReminderSchedules
                SET IsEnabled = 0, NextRunAtUtc = NULL,
                    ScheduleVersion = ScheduleVersion + 1,
                    UpdatedAt = SYSUTCDATETIME()
                OUTPUT INSERTED.ReminderScheduleId, INSERTED.ReminderType,
                       INSERTED.SourceEntity, INSERTED.SourceEntityId, INSERTED.RuleJson,
                       INSERTED.TimeZoneId, INSERTED.ScheduleVersion, INSERTED.IsEnabled,
                       INSERTED.NextRunAtUtc, INSERTED.UpdatedAt
                WHERE SourceEntity = @SourceEntity
                  AND SourceEntityId = @SourceEntityId
                  AND (JSON_VALUE(RuleJson, '$.ruleId') = CONVERT(nvarchar(20), @ReminderEventRuleId)
                       OR (JSON_VALUE(RuleJson, '$.ruleId') IS NULL AND ReminderType = @ReminderType))
                  AND IsEnabled = 1;
              `);
            for (const row of cancelled.recordset) {
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
          }

          const rulesResult = await new sql.Request(transaction)
            .input("EventType", sql.NVarChar(100), event.eventType)
            .input("SourceEntity", sql.NVarChar(100), event.sourceEntity)
            .query<EventRule>(`
              SELECT ReminderEventRuleId, EventType, ReminderType, SourceEntity,
                     DelayMinutes, TimeZoneId, MobileTemplate, MessageTemplate,
                     ProviderCode, RecurrenceIntervalValue, RecurrenceIntervalUnit, LocalTime,
                     CancelEventType, MaxOccurrences
              FROM dbo.ReminderEventRules WITH (UPDLOCK, HOLDLOCK)
              WHERE EventType = @EventType
                AND SourceEntity = @SourceEntity
                AND IsEnabled = 1
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
          for (const rule of rulesResult.recordset) {
            const mobile = renderTemplate(rule.MobileTemplate, values).trim();
            const message = renderTemplate(rule.MessageTemplate, values).trim();
            if (!mobile || mobile.length > 20) throw new Error("EVENT_INVALID_RENDERED_MOBILE");
            if (!message || message.length > 2000) throw new Error("EVENT_INVALID_RENDERED_MESSAGE");

            const dueAt = calculateInitialDueAt(
              event.occurredAtUtc, rule.DelayMinutes, rule.TimeZoneId, rule.LocalTime
            );
            const ruleJson = {
              ruleId: rule.ReminderEventRuleId,
              mobile,
              message,
              ...(rule.ProviderCode ? {providerCode: rule.ProviderCode} : {}),
              ...(rule.RecurrenceIntervalValue && rule.RecurrenceIntervalUnit
                ? {intervalValue: rule.RecurrenceIntervalValue, intervalUnit: rule.RecurrenceIntervalUnit}
                : {}),
              ...(rule.LocalTime ? {localTime: rule.LocalTime} : {}),
              ...(rule.MaxOccurrences ? {maxOccurrences: rule.MaxOccurrences} : {})
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
            createdReminderCount++;
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
            .input("CreatedReminderCount", sql.Int, createdReminderCount)
            .query(`
              UPDATE dbo.ReminderEventInbox
              SET Status = @Status,
                  CreatedReminderCount = @CreatedReminderCount,
                  ProcessedAtUtc = SYSUTCDATETIME()
              WHERE EventId = @EventId;
            `);

          await transaction.commit();
          return {tenant, duplicate: false, status, createdCount: createdReminderCount, reminders};
        } catch (error) {
          await transaction.rollback().catch(() => undefined);
          throw error;
        }
      }
    );

    let syncPending = false;
    for (const reminder of outcome.reminders) {
      try {
        await syncReminderProjection(outcome.tenant, reminder);
      } catch {
        syncPending = true;
      }
    }

    if (syncPending) {
      // Return a retryable error. The inbox is idempotent; on retry the duplicate path
      // reprojects the already-committed tenant schedules into the central scheduler.
      return NextResponse.json({
        accepted: false,
        duplicate: outcome.duplicate,
        eventId: event.eventId,
        synchronizationPending: true
      }, {status: 503});
    }

    return NextResponse.json({
      accepted: true,
      duplicate: outcome.duplicate,
      eventId: event.eventId,
      createdReminderCount: outcome.createdCount,
      synchronizationPending: false
    }, {status: outcome.duplicate ? 200 : 201});
  } catch (error) {
    return errorResponse(error);
  }
}
