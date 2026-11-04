[CmdletBinding()]
param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot "..\..\publish\win-x64"),
    [string]$ServiceName = "TaskFlowWorker"
)
$ErrorActionPreference = "Stop"

function Invoke-Sc {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & sc.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failed (exit code $LASTEXITCODE): $($Arguments -join ' ')" }
}

$resolvedPublishDirectory = (Resolve-Path $PublishDirectory -ErrorAction Stop).Path
$exePath = Join-Path $resolvedPublishDirectory "TaskFlow.Worker.exe"
if (-not (Test-Path $exePath -PathType Leaf)) { throw "Worker executable not found: $exePath. Publish win-x64 first." }

$connectionString = [Environment]::GetEnvironmentVariable("Database__ConnectionString", "Machine")
if ([string]::IsNullOrWhiteSpace($connectionString)) { throw "Set machine-level Database__ConnectionString before installing the service." }
$workerId = [Environment]::GetEnvironmentVariable("Worker__WorkerId", "Machine")
if ([string]::IsNullOrWhiteSpace($workerId)) { [Environment]::SetEnvironmentVariable("Worker__WorkerId", "$env:COMPUTERNAME-taskflow-worker", "Machine") }

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Invoke-Sc -Arguments @("create", $ServiceName, "binPath= `"$exePath`"", "start= delayed-auto", "obj= NT AUTHORITY\LocalService", "password= ")
} else {
    if ($service.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    Invoke-Sc -Arguments @("config", $ServiceName, "binPath= `"$exePath`"", "start= delayed-auto", "obj= NT AUTHORITY\LocalService", "password= ")
}
Invoke-Sc -Arguments @("description", $ServiceName, "TaskFlow SQL-backed background job processor")
Invoke-Sc -Arguments @("failure", $ServiceName, "reset= 86400", "actions= restart/60000/restart/60000/restart/60000")
Invoke-Sc -Arguments @("failureflag", $ServiceName, "1")
Write-Host "Service installed/updated. Start it with: Start-Service -Name '$ServiceName'"
Write-Host "Service account: NT AUTHORITY\LocalService"