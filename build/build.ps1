<#
  Builds GPU Info MQTT end to end: tests, self-contained publish, optional signing, installer.

    .\build\build.ps1                     # version from Directory.Build.props, unsigned
    .\build\build.ps1 -Version 1.2.0      # explicit version
    .\build\build.ps1 -Sign               # sign binaries + installer (needs signing env vars, see sign.ps1)

  Output: artifacts\installer\GpuInfoMqtt-Setup-<version>.exe
#>
param(
    [string]$Version,
    [switch]$Sign,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version }
$fileVersion = ($Version -split '[-+]')[0]
Write-Host "Building GPU Info MQTT $Version" -ForegroundColor Cyan

if (-not $SkipTests) {
    dotnet test GpuInfoMqtt.sln -c Release -p:Version=$Version
    if ($LASTEXITCODE) { throw 'Tests failed' }
}

$app = Join-Path $root 'artifacts\app'
Remove-Item (Join-Path $root 'artifacts') -Recurse -Force -ErrorAction SilentlyContinue
foreach ($proj in 'GpuInfoMqtt.Service', 'GpuInfoMqtt.Tray') {
    # Both self-contained into one folder: they share the .NET runtime files, no runtime install needed.
    dotnet publish "src\$proj\$proj.csproj" -c Release -r win-x64 --self-contained true `
        -p:DebugType=none -p:Version=$Version -o $app
    if ($LASTEXITCODE) { throw "Publish failed: $proj" }
}

$signScript = Join-Path $PSScriptRoot 'sign.ps1'
if ($Sign) {
    $ours = Get-ChildItem $app -Filter 'GpuInfoMqtt.*' | Where-Object Extension -in '.exe', '.dll'
    & $signScript @($ours.FullName)
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php' }

$isccArgs = @("/DAppVersion=$Version", "/DFileVersion=$fileVersion")
if ($Sign) {
    $isccArgs += '/DSIGN'
    # Inno's $q is a literal quote and $f the (already quoted) file. Real quotes here would be re-escaped by
    # PowerShell's native-argument passing and mangle the path.
    # Same PowerShell as this script (pwsh in CI): avoids module-path mix-ups between PowerShell 5.1 and 7.
    $shell = (Get-Process -Id $PID).Path
    $isccArgs += "/Ssigntool=`$q$shell`$q -NoProfile -ExecutionPolicy Bypass -File `$q$signScript`$q `$f"
}
& $iscc @isccArgs 'installer\GpuInfoMqtt.iss'
if ($LASTEXITCODE) { throw 'Installer build failed' }

$setup = Get-ChildItem 'artifacts\installer\*.exe' | Select-Object -First 1
Write-Host "Installer: $($setup.FullName) ($([math]::Round($setup.Length / 1MB, 1)) MB)" -ForegroundColor Green
