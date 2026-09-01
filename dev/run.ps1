# The full dev loop: tray, second-screen mode, and a real API.
#
# Checks the two things that are actually down when this fails (the API and the TLS proxy), names
# the fix rather than the exception, then launches. See DEVELOPING.md.
#
#   .\dev\run.ps1                       # launch once
#   .\dev\run.ps1 -Watch                # rebuild and relaunch on save
#   .\dev\run.ps1 -PairCode K4M2-9XPT   # redeem a web-issued pairing code on this run

[CmdletBinding()]
param(
    [switch]$Watch,
    [string]$PairCode,
    [string]$ApiBaseUrl = 'https://localhost:8443'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Test-Endpoint([string]$Url) {
    try { $null = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5; return $true }
    catch { return $false }
}

if (-not (Test-Endpoint 'http://localhost:8000/health/ready')) {
    throw "The API is not answering on 8000. Run: docker compose -f ../infra/docker-compose.yml up -d"
}

if (-not (Test-Endpoint "$ApiBaseUrl/health/ready")) {
    throw "$ApiBaseUrl is not answering, or its certificate is not trusted. Run 'node dev/local-tls-proxy.js' in its own terminal; if that is already up, redo the one-time certificate setup in DEVELOPING.md."
}

# Only one agent runs at a time, enforced by a mutex in App.OnStartup. Take the slot rather than
# launching a second instance that would exit on its own and look like a failed build.
Get-Process WarCommand.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

$env:WARCOMMAND_PROFILE = 'dev'
$env:WARCOMMAND_API_BASE_URL = $ApiBaseUrl
if ($PairCode) { $env:WARCOMMAND_PAIR_CODE = $PairCode }

if ($Watch) {
    dotnet watch run --project (Join-Path $repo 'WarCommand.Agent') --no-hot-reload
}
else {
    dotnet run --project (Join-Path $repo 'WarCommand.Agent')
}
