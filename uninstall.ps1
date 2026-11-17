#Requires -RunAsAdministrator
param(
    [string]$InstallDir = "$env:ProgramFiles\GpuInfoMqtt",
    [switch]$RemoveSettings
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'GpuInfoMqtt'

$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
    sc.exe delete $ServiceName | Out-Null
}

Get-Process GpuInfoMqtt.Tray -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'GpuInfoMqttTray' -ErrorAction SilentlyContinue

if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
if ($RemoveSettings) { Remove-Item (Join-Path $env:ProgramData 'GpuInfoMqtt') -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "Uninstalled." -ForegroundColor Green
if (-not $RemoveSettings) { Write-Host "Settings kept in $env:ProgramData\GpuInfoMqtt (use -RemoveSettings to delete)." }
