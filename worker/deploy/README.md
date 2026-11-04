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
[Environment]::SetEnvironmentVariable("SmsGateway__BaseUrl", "https://YOUR-SMS-PROVIDER-API/", "Machine")
[Environment]::SetEnvironmentVariable("SmsGateway__ApiKey", "REPLACE_ME", "Machine")
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
- This deployment package does not make the Worker production-ready. The generic SMS adapter must be integrated with the actual provider, and SQL schema compatibility plus end-to-end retry/recovery behavior must be tested.