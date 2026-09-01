#Requires -Version 7
<#
.SYNOPSIS
  Authenticode-signs one file, or does nothing when no certificate is configured.

.DESCRIPTION
  Reads the certificate from PFX_BASE64 and PFX_PASSWORD. With PFX_BASE64 empty or unset this
  exits 0 having done nothing, so the release workflow builds an unsigned installer rather than
  failing. Unsigned is the current, deliberate state: see RELEASING.md "SmartScreen".

  The .pfx is written to a temp file because signtool takes a path, and it is removed in a finally
  block so an exception cannot leave a private key on the runner's disk. Nothing is ever logged:
  neither the password nor the certificate reaches stdout, and signtool is invoked with /q.

.PARAMETER Path
  The .exe to sign.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Path
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:PFX_BASE64)) {
  Write-Host "no signing certificate configured, leaving $Path unsigned"
  exit 0
}

if (-not (Test-Path $Path)) { throw "nothing to sign at $Path" }

$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -match '\\x64\\' } |
  Sort-Object FullName -Descending |
  Select-Object -First 1

if (-not $signtool) { throw 'signtool.exe not found in the Windows SDK' }

$pfx = Join-Path ([IO.Path]::GetTempPath()) "wc-$([guid]::NewGuid()).pfx"
try {
  [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:PFX_BASE64))

  & $signtool.FullName sign /q /fd SHA256 /td SHA256 `
    /tr 'http://timestamp.digicert.com' `
    /f $pfx /p $env:PFX_PASSWORD `
    $Path
  if ($LASTEXITCODE -ne 0) { throw "signtool failed with $LASTEXITCODE" }

  & $signtool.FullName verify /pa /q $Path
  if ($LASTEXITCODE -ne 0) { throw "signature verification failed for $Path" }

  Write-Host "signed $Path"
}
finally {
  if (Test-Path $pfx) { Remove-Item $pfx -Force }
}
