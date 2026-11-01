# Building

## Prerequisites

- **.NET 10 SDK**: `winget install Microsoft.DotNet.SDK.10`
- Windows 10/11 x64 for the real thing. The server and dashboard also build and run on Linux/macOS with mock
  sensors, which is handy for dashboard work.
- PowerShell 7 (`pwsh`) for `build\package.ps1` (Windows PowerShell 5.1 works too)

## Project layout

```
SYSCOM.slnx
Directory.Build.props     shared settings (net10.0, versions, AppHostRelativeDotNet, no deps.json)
versions.json             pinned component versions for packaging
src/
  Shared/                 contracts shared by server, updater and tray (linked source, no extra DLL)
  Syscom.Server/          syscom.exe: sensors, API, WebSocket, discovery, components/updates, Windows service
  Syscom.Updater/         syscom-updater.exe: applies updates with backup and rollback
  Syscom.Tray/            syscom-tray.exe: WinForms tray app (management GUI)
web/                      dashboard (plain HTML/CSS/JS, no build step)
  js/api-reference.js     the API reference: source of the in-app API window and of docs/API.md
build/
  package.ps1             setup + full package + update assets + feed.json
  get-innosetup.ps1       finds or installs the pinned Inno Setup compiler (ISCC.exe)
  benchmark.ps1           CPU and memory of an installed SYSCOM, idle and with a dashboard at 1 s and 500 ms
install/
  syscom.iss              the setup (Inno Setup script)
  install.ps1, uninstall.ps1, README.txt   the scripted install (copied into the zip package)
tools/
  api-docs.mjs            regenerates the reference part of docs/API.md (CI checks it's up to date)
  trace-frames.ps1, analyze-frame-trace.py   frame rate diagnostics
docs/                     documentation
```

## Develop the dashboard (any OS)

```bash
dotnet run --project src/Syscom.Server -- --mock --name "Dev PC"
# prints the URL and a generated password; open http://localhost:47810
```

The dev server serves `web/` from its embedded copy. To see edits without rebuilding, copy or symlink `web/` next
to the built `syscom.dll` (e.g. `src/Syscom.Server/bin/Debug/net10.0/web`). Files there are served first and
picked up on refresh.

Run a second instance to try multi-device swiping:

```bash
dotnet run --project src/Syscom.Server -- --mock --port 47820 --name "Second PC" --config /tmp/syscom2/config.json
```

Set a known password with `--set-password <pw>` (restart the server afterwards).

## Build on Windows

```powershell
dotnet build src\Syscom.Server -r win-x64     # with LibreHardwareMonitor (LHM compile symbol)
dotnet run --project src\Syscom.Server        # run in a console; use an elevated terminal for all sensors
```

On Windows, `RuntimeIdentifier` defaults to `win-x64`, so `dotnet build` picks up LibreHardwareMonitorLib
(Windows-only, RID-specific package). Builds without a Windows RID compile it out and use mock data.

Phones and other PCs can't reach a server started with `dotnet run` until Windows Firewall lets the ports in (the
installer adds rules for `syscom.exe`; from source the server runs as `dotnet.exe`). Once, in an elevated terminal:

```powershell
New-NetFirewallRule -DisplayName 'SYSCOM dev' -Direction Inbound -Protocol TCP -LocalPort 47810,47812 -Profile Private -Action Allow
New-NetFirewallRule -DisplayName 'SYSCOM dev discovery' -Direction Inbound -Protocol UDP -LocalPort 47811 -Profile Private -Action Allow
```

The network must be a Private one (`Get-NetConnectionProfile`). Remove the rules again with
`Remove-NetFirewallRule -DisplayName 'SYSCOM dev*'`.

If the firewall is open and Chrome on an Android phone still fails (often with `ERR_TOO_MANY_RETRIES`), Chrome lacks
the Android **Nearby devices** permission, which it needs to reach LAN addresses (Settings → Apps → Chrome →
Permissions).

### Frame rate problems

To see which frame events a game sends (for example when frame generation seems to make no difference), start the
game, get into gameplay with the settings to check, and run in an administrator PowerShell:

```powershell
.\tools\trace-frames.ps1               # records 10 seconds into frame-trace\ (ignored by git)
python tools\analyze-frame-trace.py frame-trace\frames.xml
```

The script uses only Windows' `logman` and `tracerpt`. The summary (Python 3, standard library only) shows, second by
second, the frames the game renders (NVIDIA Reflex markers), the frames it presents (SYSCOM's "Frame rate") and the
flips that reach the screen. With frame generation working, presented is a multiple of rendered.

## Package

```powershell
pwsh build\package.ps1
```

Produces `dist\SYSCOM-Setup-<ver>.exe` (the setup), `dist\SYSCOM-<ver>-win-x64.zip` (the same files with
`install.ps1`) plus `syscom-app-*.zip`, `syscom-web-*.zip` and `feed.json` (update assets). See
[UPDATES.md](UPDATES.md#building-a-package). `-SkipDownloads` reuses the runtime and PawnIO from `dist\cache`;
`-NoInstaller` skips the setup.

The setup is compiled with [Inno Setup](https://jrsoftware.org/isinfo.php), pinned in `versions.json`
(`"innosetup"`). `build\get-innosetup.ps1` uses an installed copy of exactly that version, or downloads the official
installer from Inno Setup's GitHub releases, checks its SHA-256 and installs it for the current user into
`dist\cache`. The Dependencies workflow flags newer Inno Setup releases. To build the setup by hand from a staged
package folder:

```powershell
ISCC.exe /DAppVersion=0.18.0 /DPackageDir=dist\SYSCOM-0.18.0 /Odist install\syscom.iss
```

The setup does what `install.ps1` does, as a normal wizard: Program Files, the service, firewall rules on private
networks, PawnIO when the bundled one is newer, the tray app at sign-in, Start menu entries and an Installed apps
entry with an uninstaller. Silent: `/VERYSILENT /SUPPRESSMSGBOXES` (`/LOG="…"` for a log). On start, SYSCOM keeps the
Installed apps entry's version in step with in-app updates.

## API docs

The API reference lives in one place, `web/js/api-reference.js`. The dashboard's API window renders it with the PC's
own address, sensors and cards; `node tools/api-docs.mjs` writes the same reference into `docs/API.md` (between the
`api-reference` markers). After changing an endpoint, update that file and run the script; CI fails when `docs/API.md`
is out of date (`node tools/api-docs.mjs --check`).

## Benchmark

```powershell
pwsh build\benchmark.ps1 -Key syscom_…            # or -Password …; -Seconds 60 by default
pwsh build\benchmark.ps1 -Key syscom_… -Compare HWiNFO64, 'C:\Tools\LibreHardwareMonitor\LibreHardwareMonitor.exe'
```

Measures the installed `syscom.exe` (CPU as a share of the PC and of one core, working set and private memory, and
the data sent) idle, and with a dashboard streaming every sensor at 1 s and at 500 ms. `-Compare` measures other
monitoring apps the same way afterwards (a running process by name, or an .exe that it starts and closes). CI runs it
on every change with LibreHardwareMonitor's own app for comparison; the table is in the *installer* job's summary.

## CI

- `.github/workflows/ci.yml`: Windows build of all three projects (warnings are errors), JS syntax check, the API
  docs check; the setup built, installed silently, checked (service, tray task, firewall, Start menu, Installed apps,
  the API), benchmarked, upgraded over itself and uninstalled; and a Linux smoke test that starts the server with mock
  sensors and probes the API, API keys and the WebSocket.
- `.github/workflows/release.yml`: when a merge to main bumps the version in `versions.json` (or on a `v*` tag), runs
  `package.ps1` and publishes a GitHub release with the setup, the zip and the update assets.
