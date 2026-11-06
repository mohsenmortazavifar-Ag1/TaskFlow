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

## اتصال رویداد تکمیل تعمیر

Repair هنگام تغییر وضعیت سفارش به `Repaired`، رویداد `RepairCompleted` را در جدول `dbo.OutboxMessages` دیتابیس Tenant و در همان تراکنش SQL ثبت می‌کند. TaskFlow Worker این رویداد را Claim کرده و به Job مرکزی `ProcessReminderEvent` تبدیل می‌کند؛ Handler سپس آن را به `POST /api/reminder-events` می‌فرستد.

API با `ReminderEventInbox.EventId` تکرار رویداد را کنترل می‌کند و براساس Ruleهای فعال در `dbo.ReminderEventRules` یک یا چند ردیف در `dbo.ReminderSchedules` می‌سازد. Tenant Reminder Sync این Scheduleها را به دیتابیس مرکزی منتقل می‌کند و Reminder Scheduler اجرای زمان‌بندی‌شده را انجام می‌دهد.

برای راه‌اندازی end-to-end:
1. Migrationهای Outbox، Reminder Schedule و Reminder Event Rules را روی هر Tenant DB اجرا کنید.
2. جدول `dbo.ReminderEventRules` را برای `RepairCompleted` با Templateهای متناسب با کسب‌وکار تنظیم کنید.
3. Worker را با MasterRepair Registry، تنظیمات رمزگشایی رمز Tenantها، `TaskFlowApi__BaseUrl` و `TaskFlowApi__ApiKey` پیکربندی کنید.
4. `Outbox__Enabled=true` را فقط پس از تست اتصال و رمزنگاری فعال کنید.

Outbox انتقال at-least-once با ثبت مرکزی idempotent فراهم می‌کند؛ تحویل دقیقاً یک‌باره SMS توسط Provider خارجی تضمین نمی‌شود. اجرای SQL Server integration tests و تطبیق رمزنگاری MasterRepair قبل از Production الزامی است.

## وضعیت
این پروژه هنوز نیازمند تست end-to-end روی SQL Server واقعی، تأیید رمزنگاری MasterRepair و تطبیق قرارداد Provider پیامک است. موفقیت Build به‌تنهایی به معنی تأیید عملکرد Production نیست.
