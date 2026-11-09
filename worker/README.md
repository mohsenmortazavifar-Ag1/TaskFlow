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
- `Outbox__Enabled=false` در مرحله آماده‌سازی؛ فقط بعد از تکمیل preflight آن را به `true` تغییر دهید.
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

### تنظیم روی Windows Service

برای سرویس ویندوز از متغیرهای Machine استفاده کنید؛ متغیرهای `$env:` در PowerShell فقط به همان Process و فرزندان آن محدود هستند. دستورات زیر را در PowerShell با دسترسی Administrator اجرا کنید و مقادیر واقعی را از محیط استقرار دریافت کنید:

```powershell
[Environment]::SetEnvironmentVariable("TaskFlowApi__BaseUrl", "https://YOUR-TASKFLOW-HOST", "Machine")
[Environment]::SetEnvironmentVariable("TaskFlowApi__ReminderEventsPath", "/api/reminder-events", "Machine")
[Environment]::SetEnvironmentVariable("TaskFlowApi__TimeoutSeconds", "20", "Machine")

# API key باید دقیقاً با TASKFLOW_API_KEY در محیط Next.js TaskFlow یکسان باشد.
# مقدار را در Git، appsettings.json یا command history قرار ندهید.
$secureKey = Read-Host "Enter the TaskFlow TASKFLOW_API_KEY" -AsSecureString
$ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
try {
    $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
    [Environment]::SetEnvironmentVariable("TaskFlowApi__ApiKey", $plainKey, "Machine")
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
    $plainKey = $null
}

# ابتدا false بماند. پس از تأیید API، DBها، Rule و تنظیمات Tenant:
[Environment]::SetEnvironmentVariable("Outbox__Enabled", "false", "Machine")
```

همچنین `TASKFLOW_API_KEY` باید در Secret Store/Environment خود Next.js TaskFlow تنظیم شده باشد. مقدار Worker و Web باید دقیقاً یکسان باشد. بعد از تنظیم متغیرهای Machine، سرویس Worker را Restart کنید تا Environment جدید را دریافت کند.

برای فعال‌سازی Outbox فقط پس از preflight:
```powershell
[Environment]::SetEnvironmentVariable("Outbox__Enabled", "true", "Machine")
Restart-Service -Name TaskFlowWorker
```

اتصال‌های `TenantRegistry__ConnectionString` و `TenantPasswordEncryption__KeyBase64` نیز باید از Secret Store یا Environment امن Worker تأمین شوند؛ مقادیر واقعی را در مخزن ثبت نکنید.

در Repair، رویداد `RepairCompleted` در همان تراکنش تغییر وضعیت به `Repaired` در `dbo.OutboxMessages` ثبت می‌شود. Worker آن را به Job مرکزی `ProcessReminderEvent` تبدیل می‌کند. API رویداد در Tenant DB، `ReminderEventInbox` و `ReminderSchedules` را به‌صورت idempotent ثبت می‌کند؛ سرویس Tenant Reminder Sync سپس Schedule را به دیتابیس مرکزی منتقل می‌کند.

پیش از فعال‌سازی، migrationهای زیر باید روی هر Tenant DB اجرا شده باشند:
1. `database/tenant/001_outbox_schema.sql`
2. `database/tenant/002_reminder_schedules.sql`
3. `database/tenant/003_reminder_event_rules.sql`
4. `database/tenant/004_seed_repair_completed_rule.sql`

پس از Migrationها، اسکریپت `004_seed_repair_completed_rule.sql` را روی هر Tenant DB اجرا کنید. این اسکریپت Rule یکتای `RepairCompletedFollowUp` را با تأخیر پیش‌فرض 4320 دقیقه (سه روز)، TimeZone پیش‌فرض `Asia/Tehran` و ارسال یک‌باره ایجاد یا به‌روزرسانی می‌کند. TimeZone و متن پیام را قبل از اجرا با سیاست واقعی هر کسب‌وکار تطبیق دهید. Placeholderهای قابل استفاده در این event عبارت‌اند از `{{customerMobile}}`, `{{customerName}}`, `{{repairOrderNumber}}`.

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

The Worker sends `SmsBody`, `Mobiles` (array), and `SmsNumber`; the sender number is read per tenant from `dbo.TenantSmsSettings`. The provider response must contain `R_Success=true`; `DataList[0].MessageId` is saved as the provider message ID. ParsGreen's adapter currently does not establish provider-side exactly-once semantics; an ambiguous network timeout can result in a retry after provider acceptance. Validate provider retry/idempotency behavior before production.
