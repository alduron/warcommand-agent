# The overlay's own iteration loop: no docker, no TLS proxy, no API, no game.
#
# Draws the in-game surface on the primary monitor with the board from docs/design/06-overlay-ux.md,
# and rebuilds and relaunches on every save. Wardogs is not out, so this is the only way to look at
# the overlay at all. See DEVELOPING.md.
#
#   .\dev\overlay.ps1            # watch loop, rebuilds on save
#   .\dev\overlay.ps1 -Once      # single launch, no watcher
#
# The surface is click-through and cannot be focused or closed by clicking it. Quit from the tray
# icon, or stop this script.

[CmdletBinding()]
param(
    [switch]$Once
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# Only one agent runs at a time, enforced by a mutex in App.OnStartup. Take the slot rather than
# launching a second instance that would exit on its own and look like a failed build. Both names:
# the published artefact is WarCommand.exe, a dotnet run is WarCommand.Agent.
Get-Process WarCommand, WarCommand.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

$env:WARCOMMAND_OVERLAY_DEMO = '1'

if ($Once) {
    dotnet run --project (Join-Path $repo 'WarCommand.Agent')
}
else {
    # WPF's XAML hot reload is unreliable here, so this is a full rebuild-and-relaunch per save.
    dotnet watch run --project (Join-Path $repo 'WarCommand.Agent') --no-hot-reload
}
