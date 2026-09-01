# The tray's own iteration loop: no docker, no TLS proxy, no API, no window.
#
# Launches the tray icon and nothing else, and rebuilds and relaunches on every save. Right-click
# the icon for the menu; "Dev: force icon state" switches green/amber/grey with no socket, which is
# the whole point of this loop. See DEVELOPING.md.
#
#   .\dev\tray.ps1            # watch loop, rebuilds on save
#   .\dev\tray.ps1 -Once      # single launch, no watcher

[CmdletBinding()]
param(
    [switch]$Once
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# Only one agent runs at a time, enforced by a mutex in App.OnStartup. Take the slot rather than
# launching a second instance that would exit on its own and look like a failed build.
Get-Process WarCommand.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

$env:WARCOMMAND_TRAY_ONLY = '1'

if ($Once) {
    dotnet run --project (Join-Path $repo 'WarCommand.Agent')
}
else {
    # WPF's XAML hot reload is unreliable here, so this is a full rebuild-and-relaunch per save.
    dotnet watch run --project (Join-Path $repo 'WarCommand.Agent') --no-hot-reload
}
