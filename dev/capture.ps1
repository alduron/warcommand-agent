# The capture and suppression probe. Wardogs is running, this is how we learn anything about it.
#
# Builds locally only. Nothing here touches CI, and no frame is ever written to disk: the probe
# prints derived numbers and that is the whole output, per binding rule 3.
#
#   .\dev\capture.ps1 list                     # find the game window
#   .\dev\capture.ps1 scan                     # open the map, hover, and see what capture sees
#   .\dev\capture.ps1 scan -Process Wardogs -Threshold 200 -Frames 3
#   .\dev\capture.ps1 suppress -Key Mouse4     # can a hook keep the mouse out of the game
#
# See DEVELOPING.md.

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('list', 'scan', 'suppress', 'help')]
    [string]$Command = 'help',

    [string]$Process,
    [string]$Title,
    [int]$Threshold,
    [int]$Frames,
    [int]$Delay,
    [int]$Top,
    [string]$Key,
    [int]$Seconds
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'WarCommand.Agent.CaptureProbe'

$forwarded = @($Command)
if ($Process) { $forwarded += @('--process', $Process) }
if ($Title) { $forwarded += @('--title', $Title) }
if ($PSBoundParameters.ContainsKey('Threshold')) { $forwarded += @('--threshold', $Threshold) }
if ($PSBoundParameters.ContainsKey('Frames')) { $forwarded += @('--frames', $Frames) }
if ($PSBoundParameters.ContainsKey('Delay')) { $forwarded += @('--delay', $Delay) }
if ($PSBoundParameters.ContainsKey('Top')) { $forwarded += @('--top', $Top) }
if ($Key) { $forwarded += @('--key', $Key) }
if ($PSBoundParameters.ContainsKey('Seconds')) { $forwarded += @('--seconds', $Seconds) }

dotnet run --project $project -- @forwarded
