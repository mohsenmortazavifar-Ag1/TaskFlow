# TaskFlow

سامانه مرکزی مدیریت پردازش‌های پس‌زمینه، صف‌ها، پیامک و یادآوری‌ها.

## فناوری‌ها
- Next.js App Router و TypeScript
- React و lucide-react
- SQL Server از طریق پکیج mssql
- رابط فارسی و RTL
- .NET 10 Worker Service مستقل برای اجرای Jobها

## راه‌اندازی وب
1. Node.js 20.9+ و SQL Server در دسترس داشته باشید.
2. فایل .env.example را به .env.local کپی و تنظیمات اتصال را وارد کنید.
3. اسکریپت‌ها را به ترتیب اجرا کنید:
   - database/001_create_database.sql (از دیتابیس master)
   - database/002_schema.sql (در دیتابیس TaskFlow)
   - database/003_seed.sql
   - database/004_procedures.sql
4. سپس npm install و npm run dev را اجرا کنید.
5. داشبورد در http://localhost:3000 در دسترس است.

## راه‌اندازی Worker
راهنمای مستقل در [worker/README.md](worker/README.md) قرار دارد. Worker یک Process جدا از Next.js است و به .NET 10 Runtime (در حالت framework-dependent) و دسترسی SQL Server نیاز دارد.

## API
- GET /api/health اتصال SQL Server را بررسی می‌کند.
- GET /api/jobs?page=1&pageSize=20 فهرست Jobها را می‌دهد و مانند POST به Header با نام x-taskflow-api-key نیاز دارد.
- POST /api/jobs برای ثبت Job به Header با نام x-taskflow-api-key نیاز دارد.

نمونه Body:
`{"tenantId":125,"queueName":"sms","jobType":"SendSms","payload":{"smsMessageId":98765},"priority":10,"idempotencyKey":"tenant-125-event-unique-id"}`

## امنیت و وضعیت پیاده‌سازی
این نسخه پایه اجرایی است، نه سامانه آماده Production. کلید API فقط در سرویس‌های مورد اعتماد نگهداری شود. TenantId در نسخه نهایی از توکن معتبر سمت سرور استخراج شود.

Outbox Dispatcher، Reminder Scheduler، آداپتور اختصاصی Provider پیامک، احراز هویت مدیران و مدیریت کامل پیامک/صف‌ها هنوز باقی هستند. Worker فعلی چرخه Claim/Handler/Retry/Complete و Heartbeat را فراهم می‌کند؛ محدودیت‌ها در worker/README.md آمده‌اند.
