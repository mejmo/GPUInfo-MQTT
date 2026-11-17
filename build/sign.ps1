<#
  Signs files with Azure Artifact Signing (formerly Trusted Signing), the same setup as sf-querynator.

  Credentials come from the environment (never from this repo):
    AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET   - app registration (GitHub secrets)
    SIGNING_ENDPOINT, SIGNING_ACCOUNT, SIGNING_PROFILE      - signing account (GitHub variables)

  Usage: build\sign.ps1 file1.exe [file2.dll ...]
  Also used by Inno Setup as its SignTool, so the setup and uninstaller get signed too.
#>
param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Files)

$ErrorActionPreference = 'Stop'
foreach ($name in 'AZURE_TENANT_ID', 'AZURE_CLIENT_ID', 'AZURE_CLIENT_SECRET', 'SIGNING_ENDPOINT', 'SIGNING_ACCOUNT', 'SIGNING_PROFILE') {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Signing is not configured: $name is missing." }
}

if (-not (Get-Module -ListAvailable -Name TrustedSigning)) {
    Install-Module -Name TrustedSigning -MinimumVersion 0.5.0 -Force -Scope CurrentUser -Repository PSGallery
}

$paths = $Files | ForEach-Object { (Resolve-Path $_.Trim('"')).Path }
Write-Host "Signing $($paths.Count) file(s) with profile $env:SIGNING_PROFILE"

Invoke-TrustedSigning `
    -Endpoint $env:SIGNING_ENDPOINT `
    -CodeSigningAccountName $env:SIGNING_ACCOUNT `
    -CertificateProfileName $env:SIGNING_PROFILE `
    -Files ($paths -join ',') `
    -FileDigest SHA256 `
    -TimestampRfc3161 'http://timestamp.acs.microsoft.com' `
    -TimestampDigest SHA256

foreach ($p in $paths) {
    $sig = Get-AuthenticodeSignature $p
    if ($sig.Status -ne 'Valid') { throw "$p signature is $($sig.Status)" }
    Write-Host "  signed: $(Split-Path $p -Leaf)  ($($sig.SignerCertificate.Subject))"
}
