# TaskFlow .NET Worker Host

این پروژه یک Process مستقل برای Claim و اجرای Jobهای دیتابیس TaskFlow است. هدف این مرحله ایجاد چرخه اجرایی پایه است؛ نه ادعای آماده‌بودن برای Production.

## نیازمندی‌ها
- .NET 10 SDK برای Build/Publish
- دسترسی شبکه‌ای Worker به SQL Server و دیتابیس TaskFlow
- دسترسی خروجی HTTPS به SMS Gateway
- اجرای اسکریپت‌های دیتابیس TaskFlow تا مرحله `004_procedures.sql`

## تنظیمات
تنظیمات را از Environment Variables یا Secret Store تزریق کنید؛ Secret را در appsettings یا Git قرار ندهید.

متغیرهای اصلی:
- `Database__ConnectionString`: اتصال BackgroundDB
- `Worker__WorkerId`: شناسه ثابت و یکتای این Process؛ در صورت خالی بودن، شناسه خودکار ساخته می‌شود.
- `Worker__MaxConcurrency`: سقف کلی Jobهای هم‌زمان
- `Worker__PollingIntervalMilliseconds`: فاصله Poll
- `Worker__RecoveryIntervalSeconds`: فاصله بازیابی Leaseهای منقضی‌شده
- `SmsGateway__BaseUrl`: آدرس Base سرویس پیامک
- `SmsGateway__SendPath`: مسیر ارسال
- `SmsGateway__ApiKey`: کلید API

## رفتار اجرا
- Queueهای فعال از `dbo.JobQueues` خوانده می‌شوند.
- محدودیت کلی هم‌زمانی از `Worker:MaxConcurrency` و محدودیت هر صف از `JobQueues.MaxConcurrency` اعمال می‌شود.
- Job با Stored Procedure `dbo.ClaimJobs` و BatchSize=1 Claim می‌شود.
- Handler با مقدار `JobType` انتخاب می‌شود.
- موفقیت با `dbo.CompleteJob` و خطا با `dbo.FailJob` ثبت می‌شود.
- تلاش‌ها در `dbo.JobAttempts` ذخیره می‌شوند.
- Heartbeat در `dbo.WorkerInstances` ثبت می‌شود.
- سرویس داخلی Recovery به‌صورت دوره‌ای `dbo.RecoverExpiredJobs` را اجرا می‌کند.
- در زمان توقف، Job نیمه‌تمام Complete یا Fail نمی‌شود؛ Lease آن منقضی می‌شود و Recovery می‌تواند آن را دوباره Claim کند.

## قرارداد HTTP آداپتور پیامک
آداپتور فعلی یک قرارداد عمومی نمونه است، نه اتصال تأییدشده به پنل خاص. درخواست POST شامل `mobile`, `message`, `senderNumber`, `tenantId` است و Header با نام `Idempotency-Key` ارسال می‌شود. پاسخ موفق باید JSON با `{"accepted":true,"providerMessageId":"..." }` باشد. قبل از Production باید این Adapter دقیقاً با مستندات Provider واقعی تطبیق داده و تست شود.

## اجرا
از پوشه `worker/TaskFlow.Worker`:
```powershell
dotnet restore
dotnet run
```

Publish برای Windows:
```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

Publish برای Linux:
```bash
dotnet publish -c Release -r linux-x64 --self-contained false
```

پروژه به‌صورت خودکار integration مربوط به Windows Service یا systemd را بر اساس سیستم‌عامل فعال می‌کند. برای اجرای دائمی، خروجی Publish را به سرویس سیستم بسپارید و Restart policy و دسترسی حداقلی فایل/شبکه را فعال کنید.

## محدودیت‌های شناخته‌شده این مرحله
- Handler عملیاتی فقط برای `SendSms` ثبت شده است.
- قرارداد Gateway عمومی است و هنوز به Provider واقعی متصل نشده.
- زمان‌بندی Recovery انجام می‌شود، اما مانیتورینگ و هشداردهی آن هنوز کامل نیست.
- ترتیب ثابت صف‌ها می‌تواند در بار نامتوازن باعث گرسنگی صف‌های کم‌اولویت‌تر شود؛ Fair scheduling و quota واقعی هر Tenant هنوز پیاده نشده‌اند.
- Exactly-once تضمین نمی‌شود. اگر Provider پیام را قبول کند ولی Worker پیش از ثبت Complete متوقف شود، احتمال ارسال مجدد وجود دارد. پشتیبانی Provider از Idempotency-Key باید بررسی شود.
- Build و اجرای Integration Test در این محیط انجام نشده است.

## Tenant Outbox Dispatcher

The Outbox Dispatcher runs as a separate hosted service inside the same .NET Worker process. It does not run inside the queue polling loop. It reads active tenant database records from the Master database table `dbo.TenantDatabases`, builds tenant connections using `ServerName`, `DatabaseName`, `SqlLoginName` and decrypted `SqlPasswordEncrypted`. The `ConnectionStringEncrypted` column is not used by this implementation.

### Configuration

Outbox is disabled by default. Configure the following values in `appsettings.json` or override them through environment variables / a secret provider:

- `TenantRegistry:ConnectionString`: read-only connection to the Master database.
- `TenantPasswordEncryption:Algorithm`: currently `Aes256Gcm`.
- `TenantPasswordEncryption:Format`: currently `V1NonceTagCiphertextBase64`.
- `TenantPasswordEncryption:KeyBase64`: Base64-encoded 32-byte AES-256 key. Keep the real key outside source control.
- `Outbox:Enabled=true`
- `Outbox:PollingIntervalSeconds=5`
- `Outbox:BatchSizePerTenant=10`
- `Outbox:MaxTenantConcurrency=2`
- `Outbox:LeaseSeconds=300`

Environment variable examples:

```powershell
$env:Outbox__Enabled = "true"
$env:TenantRegistry__ConnectionString = "..."
$env:TenantPasswordEncryption__KeyBase64 = "..."
```

The decryptor currently implements only AES-256-GCM with the versioned envelope `v1:Base64(nonce[12] + tag[16] + ciphertext)`. Set the algorithm/format in configuration to match this contract. If the Master application currently encrypts passwords with a different algorithm, envelope, key derivation, or nonce/tag order, the decryptor must be adapted to that existing implementation before enabling Outbox. Do not change or re-encrypt stored credentials based on this sample contract.

### Tenant database migration

Run `database/tenant/001_outbox_schema.sql` once against every tenant operational database before enabling the service. This creates the generic `dbo.OutboxMessages` table and dispatch index. The migration is additive and does not alter the existing tenant `SmsLogs` table.

A business operation that needs to publish an event must insert the outbox row in the SAME transaction as its business-data changes. For SMS, use `JobType=SendSms`, `QueueName=sms`, and JSON payload fields `mobile`, `message`, and optionally `providerCode`. The dispatcher converts that payload into the central `dbo.SmsMessages` row and creates the central job with the existing handler payload shape `{ "smsMessageId": 123 }`. Other job types are transferred with their payload unchanged.

### Delivery and retry behavior

- The dispatcher refreshes the active tenant list on each poll and processes tenants with bounded concurrency.
- Tenant Outbox rows are claimed atomically using SQL update locks and a lease.
- Claim increments the attempt counter. Expired leases can be reclaimed; exhausted events move to Dead.
- Central job insertion is protected by `(TenantId, IdempotencyKey)`, with `EventId` used as the idempotency key. The central SMS row and Job are created in one central SQL transaction.
- If central publish succeeds but updating the tenant Outbox fails, the next attempt detects the existing central Job and safely marks the tenant event as published.
- A failure in one tenant is logged and does not stop processing other tenants.
- Master DB permissions should be read-only; tenant DB permissions should be limited to Outbox SELECT/UPDATE; central DB permissions should be limited to the required Jobs/SmsMessages operations.

This provides at-least-once event transfer with idempotent central job creation; it does not guarantee exactly-once SMS delivery by the external provider. Provider-side idempotency and reconciliation are still required. Build and live integration tests must pass before enabling this service in production.


## Reminder Scheduler and Handler

The Reminder Scheduler is enabled by default and runs inside the same Worker process. It reads due rows from the central `dbo.ReminderSchedules` table, creates a `dbo.ReminderOccurrences` row and an `ExecuteReminder` job in a single SQL transaction, then advances the schedule. Due rows are claimed with SQL update locks so two scheduler instances do not materialize the same due time concurrently. The unique key on `ReminderOccurrences(ReminderScheduleId, ScheduleVersion, DueAtUtc)` is the database-level duplicate guard.

The `ExecuteReminder` handler re-reads the occurrence and current schedule before acting. Disabled schedules and occurrences from an old ScheduleVersion are marked skipped. Valid occurrences create a central `SmsMessages` row and a `SendSms` job atomically; the SMS handler/provider remains responsible for actual delivery. An occurrence is marked as queued for SMS, not as provider-delivered.

### Reminder RuleJson contract

Required JSON fields are `mobile` and `message`. Optional fields:

- `providerCode`: provider selection override.
- `intervalValue` and `intervalUnit`: recurrence; units are `day`, `month`, or `year`. Omit both for a one-shot reminder.
- `localTime`: optional local wall-clock time in `HH:mm` format for the next recurrence.

Example recurring six-month filter reminder:

```json
{
  "mobile": "09120000000",
  "message": "زمان تعویض فیلتر دستگاه شما فرا رسیده است.",
  "providerCode": "default",
  "intervalValue": 6,
  "intervalUnit": "month",
  "localTime": "10:00"
}
```

Set `ReminderSchedules.NextRunAtUtc` to the first due instant in UTC and `TimeZoneId` to a valid system time-zone ID. The scheduler calculates future dates in the tenant's local time zone, then stores UTC. Daylight-saving gaps are advanced to the first valid local minute; ambiguous local times are resolved deterministically.

Environment variables include `Reminder__Enabled`, `Reminder__PollingIntervalSeconds`, and `Reminder__BatchSize`. Keep `Reminder__MaxConcurrency` at 1 for now; the scheduler serializes each materialization batch transaction.

When a reminder's business rule or target changes, update `RuleJson`/other schedule fields and increment `ScheduleVersion`. Disable a schedule by setting `IsEnabled=0`. This invalidates already queued jobs from older versions when their handler executes. A recurrence with no `intervalValue` is one-shot: `NextRunAtUtc` becomes NULL after materialization, while `IsEnabled` remains true so its already-created job can still pass the handler's enabled-schedule check. Set `IsEnabled=0` explicitly to cancel pending occurrences.

This first implementation assumes reminder definitions and customer contact/message snapshots are stored in central `dbo.ReminderSchedules.RuleJson`. It does not yet query tenant operational databases for reminder definitions, nor does it implement business-specific event triggers (for example, "three days after repair completion"). Those definitions must be created/updated by the tenant-facing business workflow and projected into the central schedule table before this scheduler can process them. Validate time-zone IDs and RuleJson at write time; malformed due rules currently fail the batch and require operator correction.
