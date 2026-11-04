[CmdletBinding(SupportsShouldProcess)]
param([string]$ServiceName = "TaskFlowWorker")
$ErrorActionPreference = "Stop"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) { Write-Host "Service '$ServiceName' does not exist."; return }
if ($service.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force
    (Get-Service -Name $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}
if ($PSCmdlet.ShouldProcess($ServiceName, "Delete Windows service")) {
    & sc.exe delete $ServiceName
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed (exit code $LASTEXITCODE)." }
    Write-Host "Service deleted. Machine-level environment variables were not removed."
}