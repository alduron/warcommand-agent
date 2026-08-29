# CLAUDE.md

`warcommand-agent`. Part of the WarCommand umbrella. The specification lives in `../docs/design/`;
the umbrella's `../CLAUDE.md` carries the rules that bind all three components.

## Before writing

Read `../docs/design/10-agent-spec.md`. Also `05-voice-grammar.md` before touching the parser or the
grammar, `06-overlay-ux.md` before drawing anything, `08-api-realtime.md` before the socket.

## Binding here

1. **Nothing enters the game process.** No `ReadProcessMemory`, `WriteProcessMemory`, `SendInput`,
   `keybd_event`, `mouse_event`, `CreateRemoteThread`, no `Present` hook, no Overwolf. Enforced by
   `ArchitectureTests`, which fails the build. Source: `Caveat_WardogsEacBlocksMemoryReads`.
2. **Never compute a firing solution.** No azimuth, elevation, range, or lead. Source:
   `Decision_WarCommandNeverComputesFiringSolutions`.
3. **`WarCommand.Agent.Core` has no platform dependency.** No `System.Windows`, no
   `System.Net.Http`, no `Windows.Graphics`, no `System.Runtime.InteropServices`. Enforced by
   `ArchitectureTests`.
4. **Snapshot the coordinate on PTT key-DOWN, never key-up.** People move the mouse while talking,
   and a request that lands where the cursor drifted is nearly impossible to diagnose from a bug
   report. Source: `Caveat_PttSnapshotCoordOnKeyDown`.
5. **Never hardcode a screen rectangle for the map readout.** It is anchored to the moving crosshair.
   Scan the map panel for near-white text runs and regex `[xy]\d+\.\d\d`. Source:
   `Insight_WardogsMapReadoutMeasured`.
6. **Never log a key code**, at any level, in any build. Return immediately from the hook for any key
   that is not a registered hotkey, and do nothing at all unless Wardogs is the foreground window.
7. **The panic key suspends every hook, capture, draw, and audio capture in one press.** Source:
   `Caveat_GlobalInputBridgeNeedsKillSwitch`.
8. **Audio and frames never touch disk and never cross the network.** The audio buffer is capped at
   8 seconds and zeroed on release. There is no debug flag that changes this.
9. **Screen capture is opt-in and off by default.** Source:
   `Decision_WarCommandNeverComputesFiringSolutions` and the M1/M2 split in `15-build-order.md`.
10. **Slots are client-side and never sent to the server.** Lowest free slot 1-9, 20 s quarantine on
    release, board always sorted by slot ascending and never re-sorted. Source:
    `Decision_WarCommandVoiceSlotsAreClientDigits`.
11. **Never add a bare voice alias without running the phonetic collision test.** "armor" is
    forbidden; it collides with "mortar". Source: `Caveat_VoiceAliasMortarArmorHomophone`.
12. **Optimistic render, reconcile on response.** A claim renders green immediately; a 409 flashes
    amber and removes the row. The user never waits on a round trip to see that their voice landed.
13. **Requests queue offline and replay. Claims do not.** A stale claim would take work somebody else
    has already handled. This asymmetry is deliberate.

## Commands

```powershell
dotnet build ; dotnet test
dotnet run --project WarCommand.Agent
dotnet publish WarCommand.Agent/WarCommand.Agent.csproj -c Release -o publish/
```

## Style

Terse XML docs stating the contract. Rationale and cross-file rules go in the knowledge graph as
anchored nodes, not in code comments. Plain ASCII.
