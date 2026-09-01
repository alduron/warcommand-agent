# Releasing warcommand-agent

One tag ships the agent. Nothing else moves: no API deploy, no web deploy, no edit to a JSON file
in another repo.

```powershell
git tag v1.4.0
git push origin v1.4.0
```

`.github/workflows/release.yml` then builds, tests, packages, and publishes a GitHub Release. Within
five minutes `warcommand.app/download` shows the new version and checksum.

## What the tag produces

| Asset | What it is |
|---|---|
| `WarCommand-Setup-<version>.exe` | The installer. Per-user, no elevation, ~75 MB. |
| `agent-release.json` | The manifest the API reads. Version, notes, installer URL, SHA-256. |

The installer produces `WarCommand.exe`, so Task Manager shows `WarCommand`, not
`WarCommand.Agent.exe`. It registers `warcommand://`, and its startup checkbox writes the same
HKCU Run value the tray's `Start with Windows` row reads, so the two can never disagree.

The version comes from the tag and from nowhere else, so a build cannot disagree with the tag it
was cut from. `v1.4.0` must be `MAJOR.MINOR.PATCH`; the workflow refuses anything else.

## How it reaches users

```
git push origin v1.4.0
  -> release.yml publishes the GitHub Release
  -> https://github.com/<owner>/warcommand-agent/releases/latest/download/agent-release.json
  -> WARCMD_AGENT_RELEASE_MANIFEST_URL in the API
  -> GET /v1/agent/latest, cached 5 minutes in Redis
  -> warcommand.app/download, and the agent's own 6-hourly update check
```

**The release must be public.** The end user's browser fetches the installer with no credentials,
so a private repository's release assets are unreachable no matter what the API is configured with.
See `infra/railway/README.md` in the umbrella for the variable, and the umbrella README for what is
and is not safe to make public here.

A prerelease is excluded from GitHub's `latest`, so tagging one moves nobody's download page. That
is the way to stage a build.

## How an installed agent updates itself

On startup and every six hours it calls `GET /v1/agent/latest`. If the published version is
strictly newer and carries an https URL and a 64-hex digest, the tray grows one row.

The install replaces the running exe, so it needs the agent to exit. That is why the row says
`restarts`, and why a running game blocks it: the row reads `on next launch` and refuses to click
while Wardogs is up. The agent downloads to `%LOCALAPPDATA%\WarCommand\updates`, hashes the file against the
published digest, and only then runs it with `/SILENT /NORESTART /UPDATE`. `/UPDATE` is ours, not
Inno's: a silent install skips the normal post-install launch, so without it an update would
replace the agent and leave nothing running.

A file whose digest does not match is deleted and the offer withdrawn rather than retried.
Nothing under `%LOCALAPPDATA%\WarCommand` is touched, so `install.id`, `tokens.dat` and the pairing survive.

**Test an update without shipping one.** Tag a prerelease, which GitHub keeps out of `latest`,
then point one machine's agent at it.

## Release notes

Optional. `installer/notes/<version>.txt` becomes both the GitHub Release body and the `notes` field
the download page renders. With no file the workflow writes a one-line placeholder.

## A dry run

`workflow_dispatch` with a version builds and packages everything and publishes nothing. Use it to
prove a packaging change before spending a tag.

## The one CI secret, and what it is

Three `IntentParser` tests read `utterances.yaml`, the parse spec shared with the API suite.
`Convention_WarCommandUtteranceFixtureIsSharedByBothSuites` forbids copying it into this repo,
because a second copy diverges on the first edit and the Python side never notices. So CI
recreates the umbrella layout: this repo is checked out into `warcommand-agent/` and the API's
fixture directory beside it.

`warcommand-api` is private, so that needs a credential. `CONTRACTS_REPO_KEY` is a **read-only
deploy key** for that one repository, not a personal access token. It cannot read any other
repo, it cannot write, it is not tied to anyone's account, and revoking it is deleting one key
under warcommand-api's Settings, Deploy keys. It is already set; nothing needs doing.

If `warcommand-api` ever becomes public, delete the key and the `ssh-key:` line, and the
checkout works on the built-in token.

## SmartScreen

Releases are unsigned today. The first few hundred downloads of an unsigned binary get Windows
SmartScreen's "unrecognized app" interstitial; `warcommand.app/download` tells users to choose
**More info**, then **Run anyway**.

To sign, set two repository secrets and nothing else changes:

- `WINDOWS_CERT_PFX_BASE64` - the code-signing certificate, base64 of the .pfx
- `WINDOWS_CERT_PASSWORD` - its password

`installer/sign.ps1` is a no-op while the first is empty, so the pipeline works signed or unsigned
with no edit. An OV certificate still accumulates SmartScreen reputation from zero; an EV one does
not, which is the only real difference for a new publisher.

## The speech model

The installer downloads the Vosk small English model at install time and unpacks it to
`%LOCALAPPDATA%\WarCommand\models\vosk-small-en-us`. It is not in the installer: 40 MB that changes
far less often than the agent would otherwise ride along in every release. A failed download is
reported and does not fail the install, and an update skips it when a model is already there.

## What an update must not break

`%LOCALAPPDATA%\WarCommand\` is never touched by the installer or the uninstaller. `install.id`,
`tokens.dat` and `config.json` live there, and `install.id` surviving an update is what keeps a
device paired. See `../docs/design/10-agent-spec.md` "Updates".
