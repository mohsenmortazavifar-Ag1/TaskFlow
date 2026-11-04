# TaskFlow .NET Worker Host

این پروژه یک Process مستقل برای Claim و اجرای Jobهای دیتابیس TaskFlow است. هدف این مرحله، ایجاد چرخه اجرایی پایه است؛ نه ادعای آماده‌بودن برای Production.

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
- هنگام توقف، Job نیمه‌تمام Complete یا Fail نمی‌شود؛ Lease آن منقضی می‌شود و Recovery می‌تواند آن را دوباره Claim کند.

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

برای اجرای دائمی، خروجی Publish را به Windows Service یا systemd بسپارید. Restart policy و دسترسی حداقلی فایل/شبکه را فعال کنید.

## محدودیت‌های شناخته‌شده این مرحله
- Handler عملیاتی فقط برای `SendSms` ثبت شده است.
- قرارداد Gateway عمومی است و هنوز به Provider واقعی متصل نشده.
- Recovery زمان‌دار باید به‌صورت سرویس مستقل/زمان‌بندی‌شده اجرا شود؛ این Worker فعلاً در صورت Crash به Lease expiration و اجرای رویه Recovery موجود متکی است.
- Exactly-once تضمین نمی‌شود. اگر Provider پیام را قبول کند ولی Worker پیش از ثبت Complete متوقف شود، احتمال ارسال مجدد وجود دارد. پشتیبانی Provider از Idempotency-Key باید بررسی شود.
- Fair scheduling و quota واقعی هر Tenant هنوز در این نسخه پیاده نشده‌اند.
- Build و اجرای Integration Test در این محیط انجام نشده است.

