# CLAUDE.md

`warcommand-agent`. Part of the WarCommand umbrella. The specification lives in `../docs/design/`;
the umbrella's `../CLAUDE.md` carries the rules that bind all three components.

## Before writing

Read `../docs/design/10-agent-spec.md`. Also `05-voice-grammar.md` before touching the parser or the
grammar, `06-overlay-ux.md` before drawing anything, `08-api-realtime.md` before the socket.

## Binding here

1. **Nothing enters the game process.** No `ReadProcessMemory`, `WriteProcessMemory`, `SendInput`,
   `keybd_event`, `mouse_event`, `CreateRemoteThread`, no `Present` hook, no Overwolf. Enforced by
   `ArchitectureTests`, which fails the build. Coordinate handoff writes the Windows clipboard and
   never synthesises the paste keystroke. Source: `Caveat_WardogsEacBlocksMemoryReads`,
   `Convention_WarCommandWritesClipboardNeverSynthesisesInput`.
2. **Fire solutions are computed, and the word is always BRACKET.** Azimuth, range, elevation in
   mils and time of flight for the L81 and SPH-2, from two map coordinates. Never rendered or
   named a "firing solution": we have no altitude, so it is flat-earth and wrong on slopes, and
   every row carries `ADJUST FROM SPOTTER`. Tables live in `contracts/ballistics.json`, never in
   code. Out of range renders `OUT OF RANGE` and never extrapolates past the last row.
   **Geometry and elevation block separately.** Azimuth and range always render; elevation and
   time of flight are withheld while that weapon's `table_confidence` is `placeholder`, rendering
   `NO FIRING TABLE`, which is what both weapons ship as today. Source:
   `Decision_WarCommandComputesFireSolutionsAsBrackets`,
   `Caveat_WarCommandFireSolutionIsTwoHalvesWithDifferentDependencies`.
3. **`WarCommand.Agent.Core` has no platform dependency.** No `System.Windows`, no
   `System.Net.Http`, no `Windows.Graphics`, no `System.Runtime.InteropServices`. Enforced by
   `ArchitectureTests`.
4. **Snapshot the coordinate on PTT key-DOWN, never key-up.** People move the mouse while talking,
   and a request that lands where the cursor drifted is nearly impossible to diagnose from a bug
   report. Source: `Caveat_PttSnapshotCoordOnKeyDown`.
5. **Never hardcode a screen rectangle, a pattern, a threshold or a scale for the map readout.** It
   is anchored to the moving crosshair, so scan the map panel using `map_readout.pattern`,
   `map_readout.near_white_threshold` and the rest of `contracts/game-profile.json`, refetched with
   its ETag. Every fact about the game lives there. Source: `Insight_WardogsMapReadoutMeasured`,
   `Convention_WarCommandGameFactsLiveInServedContracts`.
6. **Never log a key code**, at any level, in any build. Return immediately from the hook for any key
   that is not a registered hotkey, and do nothing at all unless Wardogs is the foreground window.
7. **The panic key suspends every hook, capture, draw, and audio capture in one press.** Source:
   `Caveat_GlobalInputBridgeNeedsKillSwitch`.
8. **Audio and frames never touch disk and never cross the network.** The audio buffer is capped at
   8 seconds and zeroed on release. There is no debug flag that changes this.
9. **Screen capture is opt-in and off by default**, and it is one `ICoordinateSource` among several
   rather than the mechanism. Every M1 coordinate is spoken or typed by a human. Source:
   `Caveat_WardogsEacBlocksMemoryReads`, `Decision_WarCommandM1CoordinateIsSpokenOrTypedGrid`,
   `Convention_WarCommandCoordinateAcquisitionIsBehindAnInterface`, and the M1/M2 split in
   `15-build-order.md`.
10. **Slots are client-side and never sent to the server.** **There is no quarantine**: a freed
    digit goes to the back of the reissue queue and allocation is least-recently-released, so a
    digit is never reissued until the other eight have been. When digits are scarce, admit by
    `(priority DESC, created_at ASC)`; a `low` row past `low_priority_slot_residency_s` is demoted
    to overflow and stays open, losing only its digit. The board is always sorted by slot ascending
    and never re-sorted, and a row's slot never moves while it holds one. Reset the allocator and
    its reissue order on `deployment.entered`. Source:
    `Decision_WarCommandSlotsAreLeastRecentlyReleased`, `Caveat_WarCommandSlotsResetOnDeploymentChange`.
11. **Never add a bare voice alias without running the phonetic collision test.** It is
    `warcommand-api/tests/unit/test_grammar_collisions.py`, `scripts/contracts.ps1` runs it, and a
    pair below the floor refuses to generate rather than warning. "armor" is forbidden; it collides
    with "mortar". Source: `Convention_WarCommandPhoneticFloorBlocksContractGeneration`,
    `Caveat_VoiceAliasMortarArmorHomophone`.
12. **Optimistic render, reconcile on response.** A claim renders green immediately; a 409 flashes
    amber and removes the row. The user never waits on a round trip to see that their voice landed.
13. **Requests queue offline and replay. Claims do not.** A stale claim would take work somebody else
    has already handled. This asymmetry is deliberate. Every queued submit carries the
    `captured_in_deployment_id` it was captured in; on replay the agent drops any whose deployment
    is no longer current and names them on the overlay, and the server rejects the rest with
    `409 deployment_mismatch` rather than re-stamping. Source:
    `Caveat_WarCommandOfflineQueueDrainsOntoWrongDeployment`.

## Commands

```powershell
dotnet build ; dotnet test
dotnet run --project WarCommand.Agent
dotnet publish WarCommand.Agent/WarCommand.Agent.csproj -c Release -o publish/
```

## Style

Terse XML docs stating the contract. Rationale and cross-file rules go in the knowledge graph as
anchored nodes, not in code comments. Plain ASCII.
