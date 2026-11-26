# TaskFlow

سامانه مرکزی مدیریت پردازش‌های پس‌زمینه، صف‌ها، پیامک و یادآوری‌ها.

## فناوری‌ها
- Next.js App Router و TypeScript
- SQL Server از طریق پکیج mssql
- رابط فارسی و RTL
- .NET 10 Worker Service مستقل برای اجرای Jobها

## راه‌اندازی وب
1. Node.js و SQL Server را آماده کنید.
2. فایل `.env.example` را به `.env.local` کپی و مقادیر را تنظیم کنید.
3. اسکریپت‌های مرکزی را به ترتیب اجرا کنید:
   - `database/001_create_database.sql`
   - `database/002_schema.sql`
   - `database/003_seed.sql`
   - `database/004_procedures.sql`
   - `database/005_tenant_reminder_sync.sql`
   - `database/006_sms_delivery_tracking.sql` (SMS submission and ParsGreen delivery tracking)
   - `database/008_sms_idempotency_billing_foundation.sql`
   - `database/009_sms_tenant_database_reference.sql`
   - `database/010_sms_tenant_log_outbox.sql`
4. اسکریپت‌های `database/tenant` را به ترتیب روی هر دیتابیس عملیاتی Tenant اجرا کنید.
5. سپس `npm install` و `npm run dev` را اجرا کنید.

## Worker
Worker یک Process مستقل از Next.js است. راهنمای اجرا، تنظیمات، نصب Windows Service/Linux systemd و اتصال به MasterRepair در [worker/README.md](worker/README.md) و [worker/deploy/README.md](worker/deploy/README.md) قرار دارد.

## APIهای داخلی
- `GET /api/health`: بررسی اتصال SQL Server
- `GET /api/jobs` و `POST /api/jobs`: مدیریت داخلی Jobها با Header `x-taskflow-api-key`
- `GET/POST /api/reminders`, `PATCH/DELETE /api/reminders/{id}`: مدیریت تعریف‌های Reminder
- `POST /api/reminder-events`: دریافت رویدادهای تجاری و تبدیل Ruleهای Tenant به Reminder Schedule

APIهای Reminder و رویداد فقط برای Backendهای مورد اعتماد هستند. کلید `TASKFLOW_API_KEY` را هرگز در Frontend یا متغیرهای `NEXT_PUBLIC_*` قرار ندهید. در API رویداد، `tenantDatabaseId` باید همراه `tenantId` ارسال شود تا دیتابیس عملیاتی دقیق از MasterRepair انتخاب شود.

## معماری Tenant-owned Reminder

هر Tenant مالک تعریف یادآوری، زمان‌بندی و وضعیت اجرای یادآوری‌های خودش است. دیتابیس مرکزی فقط Jobها، صف پیامک و تاریخچه ارسال/تحویل را نگه می‌دارد؛ جدول‌های مرکزی Reminder در مسیر اجرایی جدید استفاده نمی‌شوند.

جریان `RepairCompleted`:

1. Repair رویداد تکمیل تعمیر را در Outbox همان دیتابیس Tenant و در تراکنش تغییر وضعیت سفارش ثبت می‌کند.
2. Worker رویداد را با `EventId` به Job مرکزی `ProcessReminderEvent` تبدیل می‌کند.
3. `ProcessReminderEventHandler` مستقیماً به دیتابیس Tenant متصل می‌شود و در یک تراکنش، Inbox رویداد و Scheduleهای حاصل از Rule را ثبت می‌کند.
4. `TenantReminderSchedulerService` زمان‌های سررسیدشده را در دیتابیس Tenant پردازش می‌کند و Occurrence و Outbox اجرای آن را اتمیک می‌سازد.
5. Dispatcher رویداد Outbox را به Job مرکزی `ExecuteReminder` تبدیل می‌کند؛ Handler با idempotency، رکورد پیامک و Job ارسال را ایجاد می‌کند.
6. `SendSmsHandler` وضعیت یادآوری را در Tenant دوباره بررسی می‌کند و پیام را به ParsGreen می‌فرستد.
7. رویدادهای مرکزی `dbo.SmsLogs` توسط `TenantSmsLogSyncService` با شناسه یکتای رویداد به `notification.SmsLogs` همان Tenant منتقل می‌شوند.

همه مسیرهای Worker که به یک Tenant دسترسی عملیاتی دارند از قفل Exclusive مبتنی بر `sp_getapplock` استفاده می‌کنند. APIهای ایجاد، ویرایش و لغو Reminder نیز همان قفل را در سطح Transaction می‌گیرند تا تغییر Schedule با اعتبارسنجی یا ارسال Worker تداخل نداشته باشد. دو Worker نمی‌توانند یک Tenant را هم‌زمان پردازش کنند، اما Tenantهای مختلف موازی پردازش می‌شوند.

برای جزئیات تنظیمات و ترتیب کامل Migrationهای مرکزی و Tenant، به [راهنمای Worker](worker/README.md) مراجعه کنید. در این نسخه Worker برای پردازش Reminder مستقیماً به دیتابیس Tenant متصل می‌شود و به `TaskFlowApi__BaseUrl` یا `TaskFlowApi__ApiKey` وابسته نیست.

Reminder تکرارشونده باید سقف مشخص `maxOccurrences` داشته باشد. API و Scheduler تکرار نامحدود را نمی‌پذیرند؛ Migration Tenant شماره 009 داده‌های قدیمی بدون سقف را به 12 نوبت محدود می‌کند.

Outbox انتقال at-least-once با ثبت مرکزی idempotent فراهم می‌کند؛ تحویل دقیقاً یک‌باره SMS توسط Provider خارجی تضمین نمی‌شود. Build موفق جایگزین تست SQL Server واقعی و تست End-to-End نیست.

## وضعیت
این پروژه هنوز نیازمند تست end-to-end روی SQL Server واقعی، تأیید رمزنگاری MasterRepair و تطبیق قرارداد Provider پیامک است. موفقیت Build به‌تنهایی به معنی تأیید عملکرد Production نیست.
