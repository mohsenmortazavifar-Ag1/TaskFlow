# معماری TaskFlow

TaskFlow کنترل‌پلین مرکزی پردازش پس‌زمینه برای SaaS چندمستاجری است.

## اجزا
- Next.js App Router: داشبورد مدیریتی و APIهای مدیریتی.
- SQL Server: صف پایدار، وضعیت Job، تلاش‌ها، پیامک و Reminder.
- Worker Host مستقل (مرحله بعد): ترجیحاً .NET Worker Service یا Node Worker؛ پردازش سنگین در Next.js اجرا نشود.
- Tenant Outbox Dispatcher مستقل (مرحله بعد): انتقال idempotent Outbox دیتابیس Tenantها به BackgroundDB.
- Provider adapters: اتصال به پنل پیامک و کانال‌های دیگر.

## چرخه Job
1. API یا Dispatcher یک Job با IdempotencyKey یکتا ثبت می‌کند.
2. Worker با Stored Procedure به نام ClaimJobs اتمیک Claim می‌کند.
3. Handler متناسب با JobType اجرا می‌شود.
4. موفقیت با CompleteJob و خطا با FailJob ثبت می‌شود.
5. RecoverExpiredJobs، Jobهای رهاشده پس از انقضای Lease را بازیابی می‌کند.

Claim اتمیک به معنی Exactly-once delivery نیست. اگر Provider پیام را قبول کند و Worker پیش از ثبت نتیجه Crash کند، ارسال مجدد ممکن است. از idempotency سمت Provider یا reconciliation وضعیت استفاده کنید.

## چندمستاجری و عملیات
- TenantId در هر Job ذخیره شود؛ در API نهایی TenantId باید از هویت احراز‌شده سمت سرور بیاید، نه payload کلاینت.
- TenantQueueLimits و TenantSmsSettings نقطه شروع سهمیه هر Tenant هستند.
- Rate limit سراسری Provider مستقل از محدودیت هر Tenant اعمال شود؛ Fair Scheduling برای جلوگیری از گرسنگی Tenantهای کم‌مصرف ضروری است.
- زمان‌ها در SQL Server به UTC ذخیره شوند؛ منطقه زمانی Tenant برای محاسبه موعد Reminder نگهداری شود.
- Secretها فقط در Environment/Secret Store باشند و داخل Payload یا Git ذخیره نشوند.
- Next.js فقط UI و API مدیریتی را میزبانی کند. Worker و Dispatcher به‌صورت سرویس مستقل با restart policy اجرا شوند.

## محدوده این نسخه
داشبورد، اتصال SQL Server، API سلامت، API پایه Job و اسکریپت‌های دیتابیس ایجاد شده‌اند. Worker Host، Dispatcher، Scheduler، احراز هویت مدیران، مدیریت کامل صف‌ها و پیامک و تست Integration در مراحل بعدی هستند.
