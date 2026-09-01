# Developing warcommand-agent

How to build, run against the local API, pair once, and iterate, with no Wardogs installed and no
screen capture. Read `../CLAUDE.md` and `../docs/design/10-agent-spec.md` first if you have not.

## What this buys you

- **Second-screen mode** (`WarCommand.Agent.Overlay/BoardWindow`) is a normal, draggable window that
  renders the board. It needs no game window, no exclusive-fullscreen handling, and no layered
  window. It is the window the dev profile shows.
- **A dev profile** points the agent at your local API and keeps its device token between runs, so
  you register and pair once, not on every launch.
- **A fake coordinate source** (`WarCommand.Agent.Core.Dev.FakeCoordinateSource`) yields scripted
  coordinates through the same `ICoordinateSource` / `CoordinateSourceRegistry` path the real PTT
  flow uses. It is wired in only when the dev profile is active; production composition never
  constructs it.
- **A tray-only loop** that needs none of the above. See the next section.

## 0. The tray, on its own

Working on the tray icon or its menu needs no docker, no TLS proxy, no API and no window:

```powershell
.\dev\tray.ps1          # rebuilds and relaunches on every save
.\dev\tray.ps1 -Once    # single launch
```

That sets `WARCOMMAND_TRAY_ONLY=1`, which implies the dev profile and stops the startup sequence
after the icon. Right-click the icon for the menu. **`Dev: force icon state`** switches the icon
between green, amber and grey with no socket anywhere, which is how the three states are eyeballed.

**Only one agent runs at a time.** `App.OnStartup` takes a session-scoped mutex before it builds
anything; a second launch logs and exits, leaving no tray icon and no device registration. A dev
launch and a tray-only launch are still two agents, so this covers both. Both dev scripts stop a
running instance before starting, which is why a rebuild-relaunch loop never trips it.

**Windows 11 hides new tray icons.** A first launch files the icon into the overflow flyout behind
the `^` arrow, so it looks like nothing started. The agent shows a balloon saying so on every
launch. To pin it: Settings > Personalization > Taskbar > Other system tray icons, and switch
WarCommand on. Do that once and it stays on the taskbar across rebuilds.

The menu itself is not developed by launching anything. `TrayMenu.Build` in
`WarCommand.Agent.Core/Tray/TrayMenu.cs` is pure: it turns a `TrayMenuState` into rows, and
`WarCommand.Agent.Tests/Tray/TrayMenuTests.cs` covers the whole tree in milliseconds. The WinForms
`ContextMenuStrip` is a renderer over that and holds no rules. A section whose state fields are null
does not render at all, which is why the menu can ship before speech, capture, hotkeys and the
settings window exist: each one arrives as a filled-in field, not a rewrite.

## 1. Start the local stack

From the umbrella repo root:

```powershell
docker compose -f infra/docker-compose.yml up -d
curl http://localhost:8000/health/ready
```

## 2. The HTTPS blocker, and why it is not worked around

`WarCommand.Agent.Client.Http.TransportSecurity` refuses a non-https base address and a non-wss
realtime URL, unconditionally. That is a security control, not friction, and this loop does not
weaken it: the local API serves plain HTTP on 8000, so the fix is a local TLS front door, not a
relaxed check.

**One-time certificate setup** (PowerShell, from `warcommand-agent/dev/`):

```powershell
# Generate a self-signed cert for localhost. Git Bash's openssl works; so does any other OpenSSL.
openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 825 -nodes `
  -subj "//CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"

# Trust it for your Windows account only (no admin rights needed). This is the step that lets
# .NET's HttpClient (and the WebSocket client) accept the proxy's certificate.
certutil -f -user -addstore Root cert.pem
```

`certutil -addstore Root` can pop a Windows security confirmation the first time; accept it. That
prompt is why this is a manual, one-time step rather than something the agent or a script does for
you silently.

**Run the proxy** (from `warcommand-agent/dev/`, needs Node, which you already have for the web repo):

```powershell
node local-tls-proxy.js
# local-tls-proxy: https://localhost:8443 -> http://127.0.0.1:8000
```

Leave it running in its own terminal. It is a plain reverse proxy: no auth, no TLS termination
tricks beyond serving your cert, and it forwards WebSocket upgrades too so the realtime client works
the same way once that is wired up. If you would rather use a tool you already trust, `mkcert
-install` (the real one, from FiloSottile, not the npm package of the same name) plus `caddy
reverse-proxy --from localhost:8443 --to localhost:8000` does the same job.

## 3. Run the agent against it

```powershell
.\dev\run.ps1                       # checks the API and the proxy first, then launches
.\dev\run.ps1 -Watch                # rebuild and relaunch on save
.\dev\run.ps1 -PairCode K4M2-9XPT   # redeem a web-issued pairing code on this run
```

Or by hand:

```powershell
$env:WARCOMMAND_PROFILE = "dev"
$env:WARCOMMAND_API_BASE_URL = "https://localhost:8443"
dotnet run --project WarCommand.Agent
```

First run registers a device against your local API and activates it cold-start (a guest user, no
membership yet), then shows second-screen mode. That is normal, not a fault: `AgentConfig.BelongsToNothing`
says so explicitly. Every later run reuses the same device and tokens; watch
`%LOCALAPPDATA%\WarCommand\dev\logs\agent-dev.log` for `Reusing a device registration from a
previous run.` / `Reusing tokens from a previous run: no pairing needed.`

**To see a live board instead of the empty state**, either join a group from the web app running
against the same API, or set a real pairing code before the first run:

```powershell
$env:WARCOMMAND_PAIR_CODE = "K4M2-9XPT"   # from POST /v1/devices/{id}/pairing-code or the web
```

**To exercise the request flow with no game**, use the "Simulate PTT (dev)" button in the window.
It runs the same `CoordinateSourceRegistry` sweep the real PTT key-down would, lands on
`FakeCoordinateSource`, and shows the coordinate it returned.

## 4. The fast loop

```powershell
dotnet watch run --project WarCommand.Agent --no-hot-reload
```

`dotnet watch` does rebuild and relaunch this WPF app on a file save; verified in this session. What
it does **not** do well is WPF's own hot reload / edit-and-continue for XAML: it is unreliable enough
here that `--no-hot-reload` (full rebuild-and-relaunch on save) is the honest recommendation rather
than a flaky "instant" reload. That costs a few seconds per save, not a full manual stop/rebuild/run
cycle, which is the loop this whole document exists to shorten.

Storage for the dev profile lives at `%LOCALAPPDATA%\WarCommand\dev\`, a sibling of the production
`%LOCALAPPDATA%\WarCommand\` directory from 10-agent-spec.md, never the same folder: a dev token
never overwrites a production one. `tokens.dat` there is still DPAPI, `CurrentUser` scope, unchanged.

## Other commands

```powershell
dotnet build WarCommand.Agent.sln
dotnet test WarCommand.Agent.sln
dotnet publish WarCommand.Agent/WarCommand.Agent.csproj -c Release -o publish/
```

## What is still missing

The tray menu renders only the rows this build can honour: the header, the board line, second-screen
mode, the dev force-state section and Quit. The group, match, map, microphone, push-to-talk, sound,
pairing, settings and Panic rows are written and tested in `TrayMenu.Build` but stay absent until
their subsystem fills in the matching `TrayMenuState` field, so the menu never offers a click that
does nothing. Panic in particular is absent rather than greyed until `PanicSwitch.Arm()` succeeds,
which it cannot do until every `PanicSubsystem` is registered.

Realtime (the WebSocket client) is not wired into the composition root yet: the dev loop polls
`GET /v1/deployments/{id}/board` on a five-second timer instead. The proxy already forwards
WebSocket upgrades, so wiring `WarCommand.Agent.Client.Realtime.RealtimeClient` in behind the same
dev profile is the next step, not a redesign. The tray settings window, hotkeys, speech and capture
are also not part of this loop; second-screen mode plus the dev profile is the whole agent today.
