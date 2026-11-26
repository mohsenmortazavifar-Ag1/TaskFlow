# TaskFlow .NET Worker Host

این پروژه یک Process مستقل برای Claim و اجرای Jobهای دیتابیس TaskFlow است.

## نیازمندی‌ها
- .NET 10 SDK برای Build/Publish
- دسترسی شبکه‌ای Worker به SQL Server و دیتابیس TaskFlow
- دسترسی شبکه‌ای به MasterRepair و دیتابیس‌های عملیاتی Tenantها
- دسترسی خروجی HTTPS به SMS Gateway

## تنظیمات اصلی

تمام Secretها را از Environment Variables یا Secret Store تزریق کنید؛ Secret را در appsettings یا Git قرار ندهید.

- `Database__ConnectionString`: اتصال BackgroundDB مرکزی
- `TenantRegistry__ConnectionString`: اتصال read-only به MasterRepair
- `TenantPasswordEncryption__KeyBase64`: کلید واقعی سازگار با رمزنگاری رمز Tenantها
- `Worker__WorkerId`: شناسه این Process
- `Worker__MaxConcurrency`: سقف کلی Jobهای هم‌زمان
- `SmsGateway__BaseUrl=https://sms.parsgreen.ir/`, `SmsGateway__SendPath=Apiv2/Message/SendSms`, `SmsGateway__ApiKey`: اتصال ParsGreen (کلید فقط در Secret Store/Environment)

## رفتار اجرا
- Queueهای فعال از `dbo.JobQueues` خوانده می‌شوند.
- Job با Stored Procedure `dbo.ClaimJobs` Claim می‌شود.
- Handler با مقدار `JobType` انتخاب می‌شود.
- موفقیت با `dbo.CompleteJob` و خطا با `dbo.FailJob` ثبت می‌شود.
- تلاش‌ها در `dbo.JobAttempts` ذخیره می‌شوند.
- Heartbeat در `dbo.WorkerInstances` ثبت می‌شود.
- Recovery، Jobهای دارای Lease منقضی‌شده را بازیابی می‌کند.

## Tenant Outbox Dispatcher

Outbox Dispatcher در همان Worker Process اجرا می‌شود. از MasterRepair، جدول `dbo.TenantDatabases` را می‌خواند و با `ServerName`, `DatabaseName`, `SqlLoginName`, `SqlPasswordEncrypted` به دیتابیس عملیاتی Tenant متصل می‌شود. ستون `ConnectionStringEncrypted` استفاده نمی‌شود.

تنظیمات:
- `Outbox__Enabled=false` در مرحله آماده‌سازی؛ فقط بعد از تکمیل preflight آن را به `true` تغییر دهید.
- `Outbox__PollingIntervalSeconds=5`
- `Outbox__BatchSizePerTenant=10`
- `Outbox__MaxTenantConcurrency=2`
- `Outbox__LeaseSeconds=300`

پیش از فعال‌سازی، `database/tenant/001_outbox_schema.sql` را روی تمام Tenant DBها اجرا کنید. Dispatcher رکوردها را با Lease اتمیک Claim می‌کند و با `EventId` به‌عنوان idempotency key در صف مرکزی ثبت می‌کند. خطاهای موقت با backoff retry می‌شوند و بعد از اتمام تلاش‌ها به Dead می‌روند.

## Tenant-owned Reminder pipeline

یادآوری‌ها در دیتابیس عملیاتی هر Tenant نگهداری و پردازش می‌شوند؛ دیتابیس مرکزی فقط صف Jobها، پیامک‌ها و رویدادهای ارسال/تحویل را نگه می‌دارد. جدول‌های مرکزی `ReminderSchedules` و `ReminderOccurrences` در مسیر اجرایی جدید استفاده نمی‌شوند.

جریان اجرا:

1. رویداد دامنه مانند `RepairCompleted` از Outbox دیتابیس Tenant به Job مرکزی `ProcessReminderEvent` تبدیل می‌شود.
2. `ProcessReminderEventHandler` مستقیماً به دیتابیس همان Tenant وصل می‌شود؛ `ReminderEventInbox` را برای idempotency و `ReminderEventRules` را برای ساخت `ReminderSchedules` می‌خواند و همه تغییرات را در یک تراکنش Tenant ثبت می‌کند.
3. `TenantReminderSchedulerService` زمان‌بندی‌های سررسیدشده را در Tenant Claim می‌کند و ایجاد `ReminderOccurrences`، جلو بردن زمان بعدی و درج Outbox را در یک تراکنش محلی انجام می‌دهد.
4. `OutboxDispatcherService` رویداد را با کلید یکتا به Job مرکزی `ExecuteReminder` تبدیل می‌کند.
5. `ExecuteReminderHandler` وضعیت Schedule و Occurrence را از Tenant می‌خواند و با کلید idempotency یکتا، `SmsMessages` و Job نوع `SendSms` را در دیتابیس مرکزی ایجاد می‌کند.
6. `SendSmsHandler` قبل از ارسال، وضعیت یادآوری را دوباره در Tenant بررسی می‌کند و سپس پیام را برای ParsGreen ارسال می‌کند.
7. رویدادهای ثبت‌شده در central `dbo.SmsLogs` توسط `TenantSmsLogSyncService` به `notification.SmsLogs` همان Tenant منتقل می‌شوند. انتقال بعد از ثبت موفق در Tenant علامت‌گذاری می‌شود و در صورت توقف Worker قابل تکرار است. دیتابیس غیرفعال یا خطای موقت با زمان تلاش بعدی و Backoff ثبت می‌شود تا صف Tenantهای دیگر متوقف نشود.

### انحصار پردازش Tenant

تمام Worker pathهایی که به دیتابیس Tenant دسترسی عملیاتی دارند از `TenantDatabaseLock` و `sp_getapplock` با قفل Exclusive و Session ownership استفاده می‌کنند. APIهای ایجاد، ویرایش و لغو Reminder نیز همان Resource را با LockOwner=Transaction می‌گیرند؛ بنابراین تغییر Schedule نمی‌تواند هم‌زمان با اعتبارسنجی یا ارسال Worker جلو برود. اگر Worker در حال پردازش Tenant باشد، درخواست تغییر Reminder پس از 10 ثانیه انتظار با HTTP 409 برمی‌گردد. دو Worker نمی‌توانند هم‌زمان یک Tenant را پردازش کنند؛ پردازش Tenantهای متفاوت همچنان موازی است. اتصال SQL Worker تا پایان عملیات باز می‌ماند تا قفل آزاد نشود.

### محدودیت تکرار یادآوری

هر Schedule تکرارشونده باید `maxOccurrences` بین 1 تا 1000 داشته باشد. API مقدار نامحدود را نمی‌پذیرد و Scheduler نیز داده‌های قدیمی بدون سقف را اجرا نمی‌کند. Migration شماره 009، Ruleها و Scheduleهای تکرارشونده قدیمی بدون سقف را به 12 نوبت محدود می‌کند؛ این مقدار را در صورت نیاز می‌توان برای هر Rule تغییر داد.

### Migrationهای Tenant

اسکریپت‌ها را به ترتیب و روی هر دیتابیس عملیاتی Tenant اجرا کنید. اسکریپت Seed را فقط پس از ایجاد جدول Rule اجرا کنید:

1. `database/tenant/001_outbox_schema.sql`
2. `database/tenant/002_reminder_schedules.sql`
3. `database/tenant/003_reminder_event_rules.sql`
4. `database/tenant/004_reminder_event_cancellation_limits.sql`
5. `database/tenant/005_move_reminder_tables_to_notification.sql` (برای دیتابیس‌هایی که جدول‌های قدیمی در schema dbo دارند؛ قبل از اجرا وجود هم‌زمان جدول در هر دو schema را بررسی کنید)
6. `database/tenant/004_seed_repair_completed_rule.sql`
7. `database/tenant/006_order_selected_service_reminder_delay.sql`
8. `database/tenant/007_reminder_occurrences.sql`
9. `database/tenant/008_sms_delivery_logs.sql`
10. `database/tenant/009_reminder_finite_recurrence.sql`

Seed فعلی رویداد `RepairCompleted` را به یک یادآوری یک‌باره تبدیل می‌کند؛ تأخیر سرویس از فیلد `serviceReminderIntervalMonths` سفارش تعمیر می‌آید و فقط مقادیر 3، 6 یا 12 ماه پذیرفته می‌شود. رویدادهای لغو تعریف‌شده در Rule، Schedule را غیرفعال و نسخه آن را افزایش می‌دهند تا پیامک صف‌شده اما ارسال‌نشده نیز قبل از ارسال دوباره اعتبارسنجی شود.

### Migrationهای دیتابیس مرکزی

روی دیتابیس `TaskFlow` این Migrationها برای مسیر فعلی SMS لازم‌اند:

- `database/006_sms_delivery_tracking.sql`
- `database/008_sms_idempotency_billing_foundation.sql`
- `database/009_sms_tenant_database_reference.sql`
- `database/010_sms_tenant_log_outbox.sql`

برای دیتابیس‌های قدیمی که Migrationهای تاریخی `005_tenant_reminder_sync.sql` یا `007_reminder_schedule_occurrence_count.sql` را اجرا کرده‌اند، پس از اطمینان از انتقال داده‌های موردنیاز به دیتابیس Tenant، `database/011_archive_legacy_central_reminders.sql` را اجرا کنید. این Migration جداول Projection مرکزی و وضعیت Sync قدیمی را با حفظ داده‌ها به نام‌های Archive تغییر می‌دهد. Migrationهای 005 و 007 تاریخی‌اند و نباید در نصب تازه اجرا شوند؛ Schema مرکزی جدید دیگر جدول Reminder نمی‌سازد.

Billing در این نسخه فعال نیست؛ فیلدهای قیمت و هزینه صرفاً برای ثبت Snapshot در نسخه آتی آماده شده‌اند. Migration مرکزی 010 ستون‌های retry/sync تاریخچه پیامک را اضافه می‌کند.

## محدودیت‌ها و راه‌اندازی
- Outbox انتقال at-least-once با ثبت مرکزی idempotent فراهم می‌کند؛ ارسال SMS به Provider دقیقاً یک‌بار تضمین نمی‌شود.
- API Key یک credential داخلی مشترک است و باید فقط به Worker مورد اعتماد داده شود؛ هرگز در Frontend قرار نگیرد.
- رمزنگاری MasterRepair باید با decryptor فعلی AES-256-GCM v1 سازگار باشد و اتصال به هر Tenant DB باید با credential رمزگشایی‌شده تأیید شود.
- قبل از Production، SQL Server integration tests، Build و تست End-to-End اجرا شوند.
- برای ارسال واقعی SMS، `TenantSmsSettings.IsEnabled=1` و تنظیمات Provider معتبر برای Tenant آزمایشی لازم است؛ از شماره آزمایشی مجاز استفاده کنید.

اجرا از پوشه `worker/TaskFlow.Worker`:
```powershell
dotnet restore
dotnet build -c Release
dotnet run
```


## ParsGreen SMS provider

The Worker adapter is integrated with the ParsGreen Apiv2 `/Apiv2/Message/SendSms` contract used by the Repair application:
- `SmsGateway__BaseUrl=https://sms.parsgreen.ir/`
- `SmsGateway__SendPath=Apiv2/Message/SendSms`
- `SmsGateway__ApiKey`: ParsGreen API key, injected as a secret
- `SmsGateway__TimeoutSeconds=20`

The Worker sends `SmsBody`, `Mobiles` (array), and `SmsNumber`, using the ParsGreen `Authorization: basic apikey:<key>` header; the sender number is read per tenant from `dbo.TenantSmsSettings`. The `RepairCompleted` seed records `ProviderCode=ParsGreen`; this Worker version uses a single configured ParsGreen gateway for all tenants, not provider-specific routing. The SendSms response is parsed from top-level `SuccessCount` and `DataList`; `DataList[].ReqID` is saved as `ProviderMessageId`. A successful submission is not the same as delivery. The Worker calls `POST Apiv2/Message/CheckDelivery` with `{ "RecId": "<ReqID>" }`. Based on the observed provider responses, DeliveryStatus 41 is Delivered, 50 is Pending, and 0 is Undelivered; unknown codes are retained without interpretation. Each SMS is checked at most twice. The Worker API key uses `SmsGateway__ApiKey` and is separate from `TaskFlowApi__ApiKey`.

### SMS database migration and state model

Run `database/006_sms_delivery_tracking.sql` once in the central `TaskFlow` database before deploying this Worker version. It adds independent submission and delivery state columns without changing the existing `SmsMessages.Status` (0..6) contract.

- SubmissionState: 0 Pending, 1 Sending, 2 Accepted, 3 ProviderRejected, 4 NeedsReview (provider outcome ambiguous).
- DeliveryState: 0 NotScheduled, 1 PendingCheck, 2 Delivered, 3 Undelivered, 4 Unconfirmed after two checks, 5 Unknown / not checkable.
- A durable Pending -> Sending transition is committed before the external SendSms request. Any retry that sees a non-Pending state does not send again.
- Network/HTTP/invalid-response ambiguity is moved to NeedsReview, never blindly retried. A stale Sending row is also moved to NeedsReview after the configured timeout.
- Delivery checks are claimed atomically and the count is incremented before the HTTP request. A message can consume no more than two CheckDelivery requests, including failed requests. Default first and second check delays are 120 seconds.
- DeliveryStatus 50 schedules the second check only when the first check returned 50. DeliveryStatus 0 is recorded as Undelivered and never triggers an automatic resend. After the second pending/failed check, the state becomes Unconfirmed.
- ParsGreen acceptance without a ReqID is recorded as Accepted but not checkable; it is never resent.
- Legacy rows matching the previous malformed-success response are migrated to NeedsReview to prevent accidental duplicate sends. Reconcile them manually with ParsGreen before any manual action.

Configuration: `SmsDelivery__Enabled=true`, `SmsDelivery__PollingIntervalSeconds=15`, `SmsDelivery__FirstCheckDelaySeconds=120`, and `SmsDelivery__SubsequentCheckDelaySeconds=120`. The maximum check count is intentionally fixed at two and is not configurable above the provider's documented recommendation.

برای کلید ParsGreen نیز `SmsGateway__ApiKey` را از Secret Store امن به Worker تزریق کنید. شماره فرستنده در هر Tenant باید در `dbo.TenantSmsSettings.SenderNumber` تنظیم و `IsEnabled=1` باشد. کلید ParsGreen و کلید `TASKFLOW_API_KEY` دو Secret متفاوت با کاربرد متفاوت هستند.
