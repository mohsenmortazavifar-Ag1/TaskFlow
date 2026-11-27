# معماری TaskFlow

TaskFlow زیرساخت مرکزی پردازش پس‌زمینه برای یک سامانه SaaS چندمستاجری است. معماری یادآوری‌ها **Tenant-owned** است: تعریف قوانین، زمان‌بندی و نوبت‌های اجرای یادآوری در دیتابیس عملیاتی همان Tenant نگهداری و پردازش می‌شود. دیتابیس مرکزی TaskFlow مسئول صف Jobها و چرخه ارسال و پیگیری پیامک است؛ مالک تنظیمات یادآوری نیست.

## مرزبندی مسئولیت‌ها

### دیتابیس عملیاتی هر Tenant

اطلاعات و عملیات زیر در دیتابیس اختصاصی همان Tenant انجام می‌شود:

- `notification.ReminderEventInbox`: ثبت رویدادهای دریافت‌شده و جلوگیری از پردازش تکراری با `EventId`.
- `notification.ReminderEventRules`: قوانین ایجاد یادآوری بر اساس رویداد دامنه.
- `notification.ReminderSchedules`: تعریف و وضعیت زمان‌بندی یادآوری، زمان اجرای بعدی، نسخه و محدودیت تعداد اجرا.
- `notification.ReminderOccurrences`: نوبت‌های مشخص ایجادشده از Schedule.
- `notification.OutboxMessages`: انتشار پایدار رویدادهای دامنه و درخواست اجرای Occurrence.
- `notification.SmsLogs`: تاریخچه محلی پیامک‌های مرتبط با Tenant که از رخدادهای مرکزی همگام می‌شود.

سیستم تعمیرات، رویدادهایی مانند `RepairCompleted` را در Outbox دیتابیس Tenant ثبت می‌کند. APIهای مدیریت یادآوری نیز Schedule همان Tenant را می‌خوانند یا تغییر می‌دهند؛ این عملیات از قفل Tenant مشترک با Worker استفاده می‌کنند.

### دیتابیس مرکزی TaskFlow

دیتابیس مرکزی فقط زیرساخت پردازش و ارسال را نگهداری می‌کند، از جمله:

- `dbo.Jobs` و صف‌های Job برای تحویل و اجرای کارها.
- `dbo.JobAttempts`، وضعیت Lease و اطلاعات Worker برای کنترل اجرای Jobها.
- `dbo.SmsMessages` برای درخواست ارسال و وضعیت کلی پیامک.
- `dbo.SmsLogs` و ساختارهای پیگیری برای ثبت رخدادهای ارسال، پذیرش و تحویل پیامک.

جداول مرکزی قدیمی Reminder در مسیر اجرایی جدید استفاده نمی‌شوند. جداول Projection قدیمی، در دیتابیس‌هایی که Migration آرشیو روی آن‌ها اجرا شده، با حفظ داده‌ها به نام‌های Archive تغییر می‌کنند. وجود Migrationهای تاریخی در مخزن به معنی استفاده آن‌ها در نصب جدید نیست.

## اجزای اجرایی

- **Next.js App Router:** رابط مدیریتی و APIهای مدیریتی؛ پردازش زمان‌بندی و ارسال طولانی‌مدت در Next.js اجرا نمی‌شود.
- **.NET Worker Host:** Process مستقل برای Claim و اجرای Jobها، Outbox Dispatcher، پردازش رویداد یادآوری، Scheduler چندمستاجری، ارسال پیامک و همگام‌سازی تاریخچه.
- **MasterRepair Tenant Registry:** منبع فهرست دیتابیس‌های Tenant و اطلاعات اتصال آن‌ها. Worker برای اتصال عملیاتی از `ServerName`، `DatabaseName`، `SqlLoginName` و رمز عبور رمزگشایی‌شده استفاده می‌کند؛ `ConnectionStringEncrypted` مسیر اتصال این Worker نیست.
- **SMS Provider Adapter:** اتصال Worker به درگاه پیامک ParsGreen.

## جریان کامل یادآوری

### ۱. ثبت رویداد در Outbox دیتابیس Tenant

پس از تکمیل تعمیر، سیستم تعمیرات رویداد `RepairCompleted` را در `notification.OutboxMessages` همان Tenant ثبت می‌کند. ثبت Outbox باید با تغییر دامنه‌ای که رویداد را ایجاد کرده، در یک تراکنش محلی انجام شود.

### ۲. انتقال Outbox به صف مرکزی

`OutboxDispatcherService` داخل Worker، Outbox دیتابیس‌های Tenant فعال را با Lease اتمیک Claim می‌کند و رویداد را به Job مرکزی از نوع `ProcessReminderEvent` تبدیل می‌کند. `EventId` کلید idempotency انتقال است؛ بنابراین تحویل at-least-once از Outbox نباید Job منطقی تکراری ایجاد کند.

### ۳. پردازش رویداد در دیتابیس همان Tenant

`ProcessReminderEventHandler`، Tenant و دیتابیس مقصد را از Registry شناسایی می‌کند و به دیتابیس همان Tenant متصل می‌شود. سپس:

1. `notification.ReminderEventInbox` را برای `EventId` بررسی می‌کند.
2. اگر رویداد قبلاً با موفقیت پردازش شده باشد، از ایجاد مجدد یادآوری جلوگیری می‌کند.
3. قانون متناظر را از `notification.ReminderEventRules` می‌خواند.
4. در صورت فعال و منطبق بودن قانون، `notification.ReminderSchedules` را در همان دیتابیس ایجاد می‌کند.
5. Inbox، خواندن قانون، ایجاد Schedule و ثبت نتیجه پردازش را در تراکنش Tenant انجام می‌دهد.

درج Inbox و ایجاد Schedule باید اتمیک باشد. در صورت شکست تراکنش، رویداد باید قابل تلاش مجدد بماند. قید یکتای EventId نیز لایه دفاعی در برابر پردازش هم‌زمان یا تکراری است.

### ۴. زمان‌بندی و ایجاد Occurrence در Worker

`TenantReminderSchedulerService` یک سرویس دوره‌ای داخل Worker است. این سرویس فهرست Tenantهای فعال را از Registry دریافت می‌کند و دیتابیس‌های آن‌ها را با سقف هم‌زمانی پیکربندی‌شده بررسی می‌کند.

برای هر Tenant، Scheduler:

1. به دیتابیس عملیاتی Tenant متصل می‌شود.
2. قفل اختصاصی همان Tenant را می‌گیرد.
3. Scheduleهای فعال و سررسیدشده را از `notification.ReminderSchedules` انتخاب و Claim می‌کند.
4. برای هر نوبت، رکورد `notification.ReminderOccurrences` می‌سازد.
5. رویداد `ReminderOccurrenceDue` را در Outbox همان Tenant درج می‌کند.
6. شمارنده و زمان اجرای بعدی Schedule را به‌روزرسانی می‌کند؛ برای Schedule یک‌باره یا پایان‌یافته، زمان بعدی دیگر تعیین نمی‌شود.

ایجاد Occurrence، درج Outbox و به‌روزرسانی Schedule باید در یک تراکنش محلی انجام شوند تا نوبت سررسیدشده نه گم شود و نه در اثر اجرای مجدد تکثیر شود. زمان‌های موعد در UTC ذخیره و مقایسه می‌شوند؛ منطقه زمانی Tenant برای محاسبه موعد محلی استفاده می‌شود.

### ۵. انتقال Occurrence به صف مرکزی

Outbox Dispatcher رویداد `ReminderOccurrenceDue` را از دیتابیس Tenant به Job مرکزی `ExecuteReminder` تبدیل می‌کند. این مرحله نیز با کلید یکتای رویداد در برابر انتقال تکراری محافظت می‌شود.

### ۶. ایجاد درخواست پیامک در مرکز

`ExecuteReminderHandler` دوباره به دیتابیس Tenant متصل می‌شود و اعتبار Schedule و Occurrence را بررسی می‌کند. سپس با کلید idempotency مبتنی بر TenantDatabaseId و OccurrenceId، رکورد `dbo.SmsMessages` و Job نوع `SendSms` را در دیتابیس مرکزی ایجاد می‌کند.

این مرحله مرز بین مدیریت یادآوری در Tenant و مدیریت ارسال پیامک در مرکز است. تنظیمات و زمان‌بندی یادآوری به دیتابیس مرکزی منتقل نمی‌شوند.

### ۷. ارسال و کنترل لغو

`SendSmsHandler` پیش از ارسال واقعی، وضعیت Schedule و Occurrence را در دیتابیس Tenant دوباره بررسی می‌کند. اگر Schedule غیرفعال شده یا نسخه آن تغییر کرده باشد، پیامک صف‌شده نباید ارسال شود. در صورت معتبر بودن، Worker پیام را از طریق ParsGreen ارسال می‌کند.

### ۸. وضعیت ارسال و تحویل و همگام‌سازی تاریخچه

مرجع اصلی وضعیت ارسال، پذیرش توسط Provider و تحویل پیامک، ساختار مرکزی پیامک است:

- `dbo.SmsMessages`: وضعیت درخواست و وضعیت‌های مستقل Submission و Delivery.
- `dbo.SmsLogs`: رخدادهای ثبت‌شده ارسال و پیگیری Provider.
- `SmsDeliveryPollingService`: پیگیری وضعیت تحویل از ParsGreen.

پذیرفته‌شدن پیامک توسط Provider به معنی تحویل آن نیست. این دو وضعیت باید مستقل باقی بمانند و وضعیت نامشخص Provider نباید باعث ارسال مجدد کورکورانه شود.

`TenantSmsLogSyncService` رخدادهای مرکزی را به `notification.SmsLogs` دیتابیس همان Tenant منتقل می‌کند. این جدول Tenant نسخه تاریخچه محلی است؛ مرجع اصلی وضعیت پذیرش و تحویل همچنان ساختار مرکزی باقی می‌ماند. همگام‌سازی باید قابل تکرار باشد و پس از ثبت موفق در Tenant، پیشرفت آن در مرکز علامت‌گذاری شود.

## چرخه عمومی Job

1. API یا Outbox Dispatcher یک Job با `IdempotencyKey` یکتا در دیتابیس مرکزی ثبت می‌کند.
2. Worker با Stored Procedure `dbo.ClaimJobs` Job را به‌صورت اتمیک Claim می‌کند.
3. Handler متناسب با `JobType` اجرا می‌شود.
4. موفقیت با `dbo.CompleteJob` و خطا با `dbo.FailJob` ثبت می‌شود.
5. تلاش‌ها در `dbo.JobAttempts` نگهداری می‌شوند.
6. Recovery، Jobهای رهاشده پس از انقضای Lease را بازیابی می‌کند.

Claim اتمیک و Idempotency از پردازش تکراری منطقی جلوگیری می‌کنند، اما Exactly-once delivery به Provider را تضمین نمی‌کنند. اگر Provider پیام را پذیرفته باشد و Worker پیش از ثبت نتیجه متوقف شود، نتیجه ممکن است مبهم بماند. برای همین وضعیت‌های مبهم باید reconciliation شوند و ارسال مجدد خودکار و بدون بررسی انجام نشود.

## انحصار پردازش و هم‌زمانی Tenantها

تمام مسیرهای Worker که به دیتابیس عملیاتی Tenant دسترسی دارند از `TenantDatabaseLock` و `sp_getapplock` با قفل Exclusive و Session ownership استفاده می‌کنند. APIهای ایجاد، ویرایش و لغو Reminder نیز همان Resource را با LockOwner=Transaction می‌گیرند. بنابراین تغییر Schedule با اعتبارسنجی یا ارسال Worker هم‌زمان اجرا نمی‌شود.

قفل، پردازش هم‌زمان یک Tenant را بین مسیرهای Worker و API کنترل می‌کند؛ Tenantهای متفاوت می‌توانند با محدودیت هم‌زمانی پیکربندی‌شده موازی پردازش شوند. اتصال SQL باید تا پایان عملیات و آزاد شدن قفل باز بماند. خطای اتصال یا پردازش یک Tenant نباید چرخه پیمایش سایر Tenantها را متوقف کند.

## چندمستاجری، امنیت و عملیات

- TenantId و TenantDatabaseId باید از داده معتبر سمت سرور و Registry استخراج و اعتبارسنجی شوند؛ به شناسه Tenant ارسالی کلاینت اعتماد نشود.
- محدودیت صف و سهمیه هر Tenant مستقل از Rate limit سراسری Provider اعمال شود. Fair Scheduling برای جلوگیری از گرسنگی Tenantهای کم‌مصرف ضروری است.
- Secretها فقط از Environment یا Secret Store تزریق شوند؛ کلیدها، رمزها و API Keyها در Git، Payload یا Frontend قرار نگیرند.
- کلید رمزنگاری رمزهای Tenant باید با فرمت AES-256-GCM مورد استفاده MasterRepair سازگار باشد.
- Worker به SQL Server مرکزی، MasterRepair و دیتابیس‌های عملیاتی Tenant دسترسی شبکه‌ای لازم دارد.
- Next.js فقط UI و API مدیریتی را میزبانی می‌کند؛ Worker به‌صورت سرویس مستقل با Restart Policy اجرا می‌شود.

## محدوده و Migrationها

جزئیات تنظیمات Worker، ترتیب Migrationهای Tenant و مرکزی، پیش‌نیازهای فعال‌سازی Outbox، تنظیمات ParsGreen و روش اجرای Worker در [راهنمای Worker](../worker/README.md) نگهداری می‌شود. Migrationهای تاریخی مربوط به انتقال یا Projection مرکزی Reminder برای نصب تازه نیستند. روی دیتابیس‌های قدیمی، پیش از اجرای Migration آرشیو باید انتقال داده‌های موردنیاز به دیتابیس Tenant و وضعیت Migrationهای قبلی بررسی شود.

## محدودیت‌ها و تست‌های لازم

- انتقال Outbox به Job مرکزی، تحویل at-least-once با ثبت idempotent فراهم می‌کند؛ ارسال SMS دقیقاً یک‌بار تضمین‌شده نیست.
- برای Production باید تست‌های SQL Server Integration و End-to-End اجرا شوند.
- سناریوهای ضروری تست: دریافت تکراری EventId، شکست تراکنش هنگام ساخت Schedule، اجرای هم‌زمان Scheduler، توقف Worker بین ایجاد Occurrence و Dispatch، توقف بین ایجاد SmsMessage و به‌روزرسانی Occurrence، لغو Schedule پس از صف‌شدن پیامک، خطای یک Tenant در کنار Tenantهای دیگر و همگام‌سازی مجدد تاریخچه پیامک.
- برای تست ارسال واقعی، `TenantSmsSettings.IsEnabled=1` و تنظیمات معتبر ParsGreen لازم است. از شماره و حساب آزمایشی مجاز استفاده شود.
