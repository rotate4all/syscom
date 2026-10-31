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
build/package.ps1         full package + update assets + feed.json
install/                  install.ps1, uninstall.ps1, README.txt (copied into the package)
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

## Package

```powershell
pwsh build\package.ps1
```

Produces `dist\SYSCOM-<ver>-win-x64.zip` (full package) plus `syscom-app-*.zip`, `syscom-web-*.zip` and
`feed.json` (update assets). See [UPDATES.md](UPDATES.md#building-a-package). `-SkipDownloads` reuses the runtime
and PawnIO from `dist\cache`.

## CI

- `.github/workflows/ci.yml`: Windows build of all three projects (warnings are errors), JS syntax check, and a
  Linux smoke test that starts the server with mock sensors and probes the API.
- `.github/workflows/release.yml`: on a `v*` tag, runs `package.ps1` and publishes a GitHub release.
