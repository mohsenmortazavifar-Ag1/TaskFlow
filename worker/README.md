# TaskFlow .NET Worker Host

این پروژه یک Process مستقل برای Claim و اجرای Jobهای دیتابیس TaskFlow است.

## نیازمندی‌ها
- .NET 10 SDK برای Build/Publish
- دسترسی شبکه‌ای Worker به SQL Server و دیتابیس TaskFlow
- دسترسی شبکه‌ای به MasterRepair و دیتابیس‌های عملیاتی Tenantها
- دسترسی خروجی HTTPS به SMS Gateway و TaskFlow API

## تنظیمات اصلی

تمام Secretها را از Environment Variables یا Secret Store تزریق کنید؛ Secret را در appsettings یا Git قرار ندهید.

- `Database__ConnectionString`: اتصال BackgroundDB مرکزی
- `TenantRegistry__ConnectionString`: اتصال read-only به MasterRepair
- `TenantPasswordEncryption__KeyBase64`: کلید واقعی سازگار با رمزنگاری رمز Tenantها
- `Worker__WorkerId`: شناسه این Process
- `Worker__MaxConcurrency`: سقف کلی Jobهای هم‌زمان
- `SmsGateway__BaseUrl`, `SmsGateway__SendPath`, `SmsGateway__ApiKey`: اتصال Provider پیامک
- `TaskFlowApi__BaseUrl`: آدرس پایه Next.js TaskFlow
- `TaskFlowApi__ReminderEventsPath=/api/reminder-events`
- `TaskFlowApi__ApiKey`: همان کلید سروری که TaskFlow API در `TASKFLOW_API_KEY` می‌پذیرد
- `TaskFlowApi__TimeoutSeconds=20`

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
- `Outbox__Enabled=true`
- `Outbox__PollingIntervalSeconds=5`
- `Outbox__BatchSizePerTenant=10`
- `Outbox__MaxTenantConcurrency=2`
- `Outbox__LeaseSeconds=300`

پیش از فعال‌سازی، `database/tenant/001_outbox_schema.sql` را روی تمام Tenant DBها اجرا کنید. Dispatcher رکوردها را با Lease اتمیک Claim می‌کند و با `EventId` به‌عنوان idempotency key در صف مرکزی ثبت می‌کند. خطاهای موقت با backoff retry می‌شوند و بعد از اتمام تلاش‌ها به Dead می‌روند.

## Business Event -> Reminder

Handler با نام `ProcessReminderEvent` رویدادهای دامنه را از صف مرکزی به `POST /api/reminder-events` می‌فرستد. این Handler:
- TenantId داخل payload را با TenantId خود Job مقایسه می‌کند.
- فقط HTTPS را در محیط غیرمحلی قبول می‌کند.
- API key را فقط در Header `x-taskflow-api-key` می‌فرستد.
- هر پاسخ 2xx از جمله پاسخ duplicate را موفق تلقی می‌کند.
- timeout و پاسخ غیر 2xx را به Queue برمی‌گرداند تا Job طبق سیاست Retry دوباره اجرا شود.

نمونه تنظیم Environment:
```powershell
$env:Outbox__Enabled = "true"
$env:TaskFlowApi__BaseUrl = "https://taskflow.example.com"
$env:TaskFlowApi__ApiKey = "<secret>"
$env:TenantRegistry__ConnectionString = "<MasterRepair read-only connection>"
$env:TenantPasswordEncryption__KeyBase64 = "<base64 key>"
```

در Repair، رویداد `RepairCompleted` در همان تراکنش تغییر وضعیت به `Repaired` در `dbo.OutboxMessages` ثبت می‌شود. Worker آن را به Job مرکزی `ProcessReminderEvent` تبدیل می‌کند. API رویداد در Tenant DB، `ReminderEventInbox` و `ReminderSchedules` را به‌صورت idempotent ثبت می‌کند؛ سرویس Tenant Reminder Sync سپس Schedule را به دیتابیس مرکزی منتقل می‌کند.

پیش از فعال‌سازی، migrationهای زیر باید روی هر Tenant DB اجرا شده باشند:
1. `database/tenant/001_outbox_schema.sql`
2. `database/tenant/002_reminder_schedules.sql`
3. `database/tenant/003_reminder_event_rules.sql`

همچنین باید برای `EventType=RepairCompleted` یک Rule فعال در `dbo.ReminderEventRules` همان Tenant وجود داشته باشد. Placeholderهای قابل استفاده در این event عبارت‌اند از `{{customerMobile}}`, `{{customerName}}`, `{{repairOrderNumber}}`.

## محدودیت‌ها و راه‌اندازی
- Outbox انتقال at-least-once با ثبت مرکزی idempotent فراهم می‌کند؛ ارسال SMS به Provider دقیقاً یک‌بار تضمین نمی‌شود.
- API Key یک credential داخلی مشترک است و باید فقط به Worker مورد اعتماد داده شود؛ هرگز در Frontend قرار نگیرد.
- رمزنگاری MasterRepair باید با decryptor فعلی AES-256-GCM v1 سازگار باشد. این سازگاری باید قبل از فعال‌سازی در محیط واقعی تست شود.
- SQL Server integration tests و build باید قبل از Production اجرا شوند.

اجرا از پوشه `worker/TaskFlow.Worker`:
```powershell
dotnet restore
dotnet build -c Release
dotnet run
```
