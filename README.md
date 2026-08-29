# warcommand-agent

The WarCommand client: a Windows tray app plus a transparent always-on-top overlay. Hotkey, local
speech recognition, and opt-in map capture.

.NET 8, WPF, Windows 10 1903 or later (Windows Graphics Capture requires it).

## Run it

```powershell
dotnet build
dotnet run --project WarCommand.Agent
dotnet test
dotnet publish WarCommand.Agent/WarCommand.Agent.csproj -c Release -o publish/
```

## Assemblies

| Assembly | Owns | Target |
|---|---|---|
| `WarCommand.Agent` | Tray host, composition root, settings UI, `warcommand://` URI handler | net8.0-windows |
| `WarCommand.Agent.Core` | Board state, slot allocator, intent model, PTT state machine. **No Windows API, no HTTP.** | net8.0 |
| `WarCommand.Agent.Overlay` | Layered window, rendering, sound | net8.0-windows |
| `WarCommand.Agent.Input` | Global hotkeys, PTT, panic key, foreground scoping | net8.0-windows |
| `WarCommand.Agent.Speech` | `ISpeechEngine`, Vosk, grammar compiler | net8.0-windows |
| `WarCommand.Agent.Capture` | Windows Graphics Capture, readout scanner, glyph atlas | net8.0-windows |
| `WarCommand.Agent.Client` | HTTP + WebSocket client, token store, reconnect | net8.0 |
| `WarCommand.Agent.Tests` | Everything, plus the architecture guard | net8.0-windows |

`Core` having no platform dependency is the whole test strategy. The slot allocator, the PTT state
machine, and the intent parser are the parts most likely to be wrong, and all three are testable with
no window, no microphone, and no server. `ArchitectureTests` enforces it.

## What this app must never do

`ArchitectureTests.No_source_file_reaches_into_the_game_process` fails the build on any of:

```
ReadProcessMemory  WriteProcessMemory  VirtualAllocEx  CreateRemoteThread  NtCreateThreadEx
SendInput  keybd_event  mouse_event
SetWindowsHookEx with a non-zero module handle
IDXGISwapChain (Present hook)   Overwolf
```

Wardogs ships kernel-level Easy Anti-Cheat. Everything stays out of process: Windows Graphics
Capture on the game window, which is the same mechanism OBS Window Capture uses. Full reasoning in
`../docs/design/12-security.md`.

The app also never computes a firing solution. It relays a coordinate a human read off the game's
own map to another human.

## Storage

```
%LOCALAPPDATA%\WarCommand\
  install.id      128-bit random hex, written once, survives updates
  config.json     the config payload from the API, plus local settings
  tokens.dat      DPAPI-encrypted, CurrentUser scope
  logs\           rolling, 7 days, 10 MB cap
  models\vosk-small-en-us\
```

Tokens are never written to `config.json`, never logged, never printed. Key codes are never logged at
any level in any build.

## Specification

`../docs/design/10-agent-spec.md` is the spec. Also `05-voice-grammar.md` for the grammar,
`06-overlay-ux.md` for the overlay and slot rules, `08-api-realtime.md` for the socket.
