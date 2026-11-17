#Requires -RunAsAdministrator
<#
  Builds and installs GPU Info MQTT:
    - Windows service "GpuInfoMqtt" (LocalSystem, delayed auto-start, restarts on failure)
    - Tray app started at logon for every user
  Re-run to upgrade; settings in C:\ProgramData\GpuInfoMqtt are kept.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\GpuInfoMqtt"
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'GpuInfoMqtt'
$Root = $PSScriptRoot
$Staging = Join-Path $Root 'publish'

Write-Host "Publishing..." -ForegroundColor Cyan
foreach ($proj in 'GpuInfoMqtt.Service', 'GpuInfoMqtt.Tray') {
    dotnet publish "$Root\src\$proj\$proj.csproj" -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=none -o $Staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }
}

Write-Host "Stopping running instances..." -ForegroundColor Cyan
$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
Get-Process GpuInfoMqtt.Tray -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Copying to $InstallDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item "$Staging\GpuInfoMqtt.Service.exe", "$Staging\GpuInfoMqtt.Tray.exe" $InstallDir -Force
$serviceExe = Join-Path $InstallDir 'GpuInfoMqtt.Service.exe'
$trayExe = Join-Path $InstallDir 'GpuInfoMqtt.Tray.exe'

# Settings folder: only SYSTEM and Administrators may access it (the tray app goes through the service).
# The service also enforces this ACL itself on every start.
$dataDir = Join-Path $env:ProgramData 'GpuInfoMqtt'
New-Item -ItemType Directory -Force $dataDir | Out-Null
icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
icacls $dataDir /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
if (Get-ChildItem $dataDir) { icacls "$dataDir\*" /reset /C /Q | Out-Null }   # files inherit the restricted folder ACL

if (-not $svc) {
    Write-Host "Creating service..." -ForegroundColor Cyan
    sc.exe create $ServiceName binPath= "`"$serviceExe`"" start= delayed-auto DisplayName= "GPU Info MQTT" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed" }
    sc.exe description $ServiceName "Publishes NVIDIA GPU power, memory, temperature and fan to Home Assistant over MQTT." | Out-Null
}
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/10000/restart/60000 | Out-Null
Start-Service $ServiceName

# Tray app at logon for all users.
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'GpuInfoMqttTray' -Value "`"$trayExe`""

# Start the tray now, non-elevated, in the current user's session.
Start-Process explorer.exe -ArgumentList "`"$trayExe`""

Write-Host "Installed. Service status: $((Get-Service $ServiceName).Status). Click the tray icon to configure." -ForegroundColor Green
