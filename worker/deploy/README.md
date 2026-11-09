# Worker deployment

The Worker is one .NET process. Windows is the current deployment target; the Linux unit is included for a future deployment using the same application.

## Windows Server

### Publish
Run from the repository root:
```powershell
dotnet publish .\worker\TaskFlow.Worker\TaskFlow.Worker.csproj -c Release -r win-x64 --self-contained false -o .\worker\publish\win-x64
```
Install the .NET 10 runtime on the target server and copy the complete publish directory to a stable path such as `C:\Services\TaskFlowWorker`.

### Configure
Open elevated PowerShell and configure machine-level settings before starting the service:
```powershell
[Environment]::SetEnvironmentVariable("Database__ConnectionString", "Server=YOUR_SQL_SERVER;Database=TaskFlow;User ID=TASKFLOW_WORKER;Password=REPLACE_ME;Encrypt=True;TrustServerCertificate=False;", "Machine")
[Environment]::SetEnvironmentVariable("Worker__WorkerId", "$env:COMPUTERNAME-taskflow-worker", "Machine")
[Environment]::SetEnvironmentVariable("Worker__MaxConcurrency", "3", "Machine")
[Environment]::SetEnvironmentVariable("Worker__PollingIntervalMilliseconds", "2000", "Machine")
[Environment]::SetEnvironmentVariable("SmsGateway__BaseUrl", "https://sms.parsgreen.ir/", "Machine")
[Environment]::SetEnvironmentVariable("SmsGateway__SendPath", "Apiv2/Message/SendSms", "Machine")
[Environment]::SetEnvironmentVariable("TaskFlowApi__BaseUrl", "https://YOUR-TASKFLOW-HOST", "Machine")
[Environment]::SetEnvironmentVariable("TaskFlowApi__ReminderEventsPath", "/api/reminder-events", "Machine")
[Environment]::SetEnvironmentVariable("TaskFlowApi__TimeoutSeconds", "20", "Machine")
[Environment]::SetEnvironmentVariable("Outbox__Enabled", "false", "Machine")
[Environment]::SetEnvironmentVariable("Reminder__Enabled", "true", "Machine")
[Environment]::SetEnvironmentVariable("Reminder__TenantSyncEnabled", "true", "Machine")
# Also provide TenantRegistry__ConnectionString and TenantPasswordEncryption__KeyBase64.
# Inject TaskFlowApi__ApiKey and SmsGateway__ApiKey from the approved secret store.
```
Use a dedicated SQL login with only required permissions; do not use `sa`. The service runs as `NT AUTHORITY\LocalService`. For a remote SQL Server, SQL authentication is the straightforward option unless a domain service identity and integrated authentication are deliberately configured.

Machine environment variables are not a dedicated secret vault. Restrict administrator access and use an approved secret-management solution for production credentials. After changing machine-level environment variables, restart the server (or otherwise ensure the Service Control Manager environment is refreshed) before expecting a Windows service process to see the new values.

### Install and start
From elevated PowerShell:
```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\worker\deploy\windows\install-service.ps1 -PublishDirectory "C:\Services\TaskFlowWorker"
Start-Service -Name TaskFlowWorker
Get-Service -Name TaskFlowWorker
```
The installer configures delayed automatic startup and service recovery actions. It does not create or migrate the database.

### Verify
```powershell
Get-Service -Name TaskFlowWorker
Get-WinEvent -LogName Application -MaxEvents 100 | Where-Object { $_.ProviderName -like "*TaskFlow*" } | Select-Object TimeCreated, LevelDisplayName, Message
```
Also confirm `dbo.WorkerInstances.LastHeartbeatAt` is updating and complete a controlled test Job. Validate SMS using a provider test destination, not a real customer.

### Update / uninstall
Stop the service, replace published files, run the installer again, and restart:
```powershell
Stop-Service TaskFlowWorker
# Replace files in C:\Services\TaskFlowWorker
.\worker\deploy\windows\install-service.ps1 -PublishDirectory "C:\Services\TaskFlowWorker"
Start-Service TaskFlowWorker
```
Remove only the service registration with `worker\deploy\windows\uninstall-service.ps1`. Machine environment variables are intentionally preserved.

## Linux (future target)

Publish on a build machine:
```bash
dotnet publish worker/TaskFlow.Worker/TaskFlow.Worker.csproj -c Release -r linux-x64 --self-contained false -o worker/publish/linux-x64
```
On the Linux host, install the .NET 10 runtime, create a restricted account, copy publish output to `/opt/taskflow-worker`, and install the systemd unit:
```bash
sudo useradd --system --home /opt/taskflow-worker --shell /usr/sbin/nologin taskflow
sudo install -d -o taskflow -g taskflow /opt/taskflow-worker
sudo install -d -m 0750 -o root -g taskflow /etc/taskflow
sudo install -m 0644 worker/deploy/linux/taskflow-worker.service /etc/systemd/system/taskflow-worker.service
sudo install -m 0600 -o root -g taskflow /dev/null /etc/taskflow/worker.env
sudo systemctl daemon-reload
sudo systemctl enable --now taskflow-worker
sudo systemctl status taskflow-worker
```
Populate `/etc/taskflow/worker.env` with the same double-underscore keys (for example `Database__ConnectionString=...`, `Worker__WorkerId=...`). Keep it restricted to root and the `taskflow` group.

## Operational constraints

- Run one process per unique `WorkerId`; never start duplicate processes with the same ID.
- `Worker__MaxConcurrency` is a hard upper bound, not a throughput guarantee. Start conservatively and load-test before increasing it.
- Allow the Worker host to connect to SQL Server and make outbound HTTPS calls to the SMS provider.
- Configure database backups, monitoring, alerting, and provider-side idempotency before production.
- This deployment package does not make the Worker production-ready. The Worker includes a ParsGreen Apiv2 adapter. Configure the ParsGreen API key in `SmsGateway__ApiKey`; configure a valid sender number and `IsEnabled=1` in each tenant row of `dbo.TenantSmsSettings`. Configure `TaskFlowApi__ApiKey` to exactly match the Next.js server-side `TASKFLOW_API_KEY`. The TaskFlow web host must also have `MASTER_DB_SERVER`, `MASTER_DB_USER`, `MASTER_DB_PASSWORD`, `MASTER_DB_NAME`, and `TENANT_PASSWORD_ENCRYPTION_KEY_BASE64` configured so its event API can resolve/decrypt tenant credentials. Keep all secrets in the approved secret store. SQL schema compatibility plus end-to-end retry/recovery behavior must be tested.
### Outbox activation gate

Keep `Outbox__Enabled=false` until all of the following are verified:
1. Central TaskFlow migrations `001` through `005` have been applied.
2. Tenant migrations `001_outbox_schema.sql`, `002_reminder_schedules.sql`, and `003_reminder_event_rules.sql` have been applied to every in-scope Repair tenant database.
3. `004_seed_repair_completed_rule.sql` has been applied to the designated test tenant and the returned row is enabled with the expected event/source and delay.
4. The Worker can connect to MasterRepair, decrypt a test tenant password, and open that tenant database using the configured TLS settings.
5. The TaskFlow API is reachable over HTTPS and accepts a controlled authenticated test request.
6. The ParsGreen key is valid and the tenant sender number is enabled; first send only to an explicitly authorized test mobile.
7. The complete test event is visible in tenant `OutboxMessages`, central `Jobs`, tenant `ReminderEventInbox` and `ReminderSchedules`, central `ReminderSchedules` and `ReminderOccurrences`, then central `SmsMessages` and `SmsLogs`.

Only then set `Outbox__Enabled=true` and restart the Worker. The rule's default delay is three days; use a separate non-production test rule/database for a short delay. Do not shorten the production rule to accelerate testing.
