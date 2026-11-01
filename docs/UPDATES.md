# Updates and releases

SYSCOM ships as one package with everything it needs, but each part can be updated on its own, from the tray app,
from that part's official source.

## Components

| Id | Component | Installed version from | Latest version from | How it's applied |
| --- | --- | --- | --- | --- |
| `syscom` | Server, updater, tray app | assembly version | `feed.json` in this repo's latest GitHub release | updater swaps files, restarts |
| `web` | Web dashboard | `web\version.json` | `feed.json` | swapped in place, live, no restart |
| `lhm` | LibreHardwareMonitorLib + its dependencies | DLL product version | nuget.org (newest stable) | updater swaps DLLs, restarts |
| `pawnio` | PawnIO driver | registry (`Uninstall\PawnIO`) | api.github.com `namazso/PawnIO.Setup` latest release | runs the official setup silently, then restarts |
| `dotnet` | Private .NET runtime in `runtime\` | `Environment.Version` | Microsoft's `release-metadata/<channel>/releases.json` | new patch installed side by side, restart rolls forward; old patches pruned |

### Health

Each component reports **Ok / Warning / Error** with a reason, for example:

- LibreHardwareMonitor: library missing, or failed to load (error text shown), or loaded but found no sensors
- PawnIO: not installed (warning: CPU temps missing), or older than `MinPawnIoVersion` (error: security)
- .NET: the channel reached end of support (error: needs a SYSCOM update moving to a newer .NET)
- Web: `web\` missing, so the copy built into the exe is in use (warning)

The tray icon turns amber or red when any component is unhealthy, and pops a notification when updates are available.

## Automatic updates

The server checks every source every `UpdateCheckHours` (6 by default) and, depending on `AutoUpdate` (tray app main window →
**Install updates automatically**), installs what it found by itself:

| `AutoUpdate` | Installs on its own | Tray app label |
| --- | --- | --- |
| `syscom` (default) | SYSCOM and the web dashboard, from this repository's releases | SYSCOM and its dashboard (recommended) |
| `all` | every component, LibreHardwareMonitor, PawnIO and .NET included | Every component |
| `off` | nothing: updates are only announced, and installed with one click | Off: only tell me |

- An update that restarts SYSCOM (everything but the web dashboard) waits until no dashboard is connected, for at
  most 24 hours, so a wall display doesn't block it forever. The web dashboard is swapped live.
- Installation goes through the same checksum checks, backup and health-checked rollback as a click in the tray app.
  A version that failed or was rolled back is not tried again automatically (a newer one is).
- Open dashboards check the PC they were opened from every 15 minutes and reload themselves when it has a newer
  dashboard, once nothing is being edited.
- `AutoCheckUpdates: false` turns checking (and so automatic installing) off.

## Verification

Nothing is installed without the publisher's checksum:

- SYSCOM / web zips: SHA-256 from `feed.json`
- NuGet packages: SHA-512 `packageHash` from the nuget.org catalog entry (every dependency too)
- .NET runtime zips: SHA-512 from Microsoft's release metadata
- PawnIO setup: SHA-256 `digest` GitHub publishes for release assets

## Apply and roll back

1. The server downloads and verifies everything into `%ProgramData%\SYSCOM\updates\<timestamp>\<component>\`.
2. In-process parts are applied right away (web dashboard, PawnIO setup).
3. If files in the install folder must change (or a restart is needed), the server copies `syscom-updater` into that
   run folder, writes `plan.json` and starts it. When SYSCOM itself is updated, that is the **new** version's updater
   (unless it needs a newer .NET than the one installed).
4. The updater stops the **SYSCOM** service (and waits until its process has exited, not only until Windows reports it
   stopped), backs up every file it will overwrite into `backup\`, copies the
   new files, starts the service and polls `http://127.0.0.1:<port>/api/health` for up to 60 s. If the service
   stops on its own, it fails fast instead of waiting.
   - Files another program has open can't be overwritten on Windows; the running tray app's are. Those are renamed
     to `<name>.syscom-old` and the new file takes their place. The server deletes leftovers when it starts, the
     tray app its own. The tray app notices its files changed and, once SYSCOM is back on that version, restarts
     itself into it.
5. **Not healthy?** It stops the service, restores the backup (renamed files go back under their own name),
   deletes files that were new, and starts the old version again.
6. The outcome goes to `updates\last-result.json` and `updates\<timestamp>\updater.log`. The server reads it as soon
   as it changes; the tray app, which keeps its buttons disabled and a progress bar running from the click until
   then, shows it. The 5 newest update folders are kept.

**Updating from 0.17.0 or older:** those versions' updater can't swap the tray app's files, so their in-app update
of SYSCOM rolls back while the tray app runs (the web dashboard updates fine). Run the latest
`SYSCOM-Setup-<version>.exe` once over it (or the zip's `install.ps1`, 0.17.1 or newer). Password, settings and
dashboards stay. Later versions update from the tray app or by themselves.

**Installed with the setup or with install.ps1:** both install to the same place in the same way, so in-app updates
work the same. Over a copy installed with the setup, SYSCOM keeps the version shown in Settings → Apps → Installed
apps in step after each update; running a newer setup over either kind of install upgrades it in place.

Updating LibreHardwareMonitor in place works because the install has no `deps.json` pinning. If a new LHM version
breaks API compatibility with the SYSCOM build, the health check fails and the update rolls back. A new SYSCOM
release built against that LHM version fixes it.

## Building a package

`versions.json` pins what the full package contains:

```json
{
  "syscom": "0.18.1",
  "web": "0.18.0",
  "dotnet": { "channel": "10.0", "runtime": "latest" },
  "librehardwaremonitor": "0.9.6",
  "pawnio": "latest",
  "windowsServices": "10.0.14",
  "innosetup": "7.1.1"
}
```

Bump a value and run `pwsh build\package.ps1`. The script builds SYSCOM against the pinned LHM version, downloads
and verifies the runtime and PawnIO, and writes to `dist\`:

- `SYSCOM-Setup-<ver>.exe`: the setup (what most people download; built with the pinned Inno Setup)
- `SYSCOM-<ver>-win-x64.zip`: the same files with the scripted install (extract, then run `install.ps1`)
- `syscom-app-<ver>.zip`, `syscom-web-<webver>.zip`: update assets
- `feed.json`: the feed installed copies read

## Cutting a release

1. In the pull request, bump `versions.json` (`syscom`, and `web` if the dashboard changed) and add the version's
   section to `CHANGELOG.md` (`## [x.y.z] - date`). Feature requests bump the minor version, fixes the patch.
2. Merge it to `main`. The **Release** workflow sees the new version, builds the package on Windows and publishes the
   GitHub release `vx.y.z` on that commit, with all files from `dist\` and the changelog section as release notes.
   (Pushing a `v*` tag by hand still works; a version that already has a release is skipped.)
3. Installed PCs pick it up at their next check (see Automatic updates), or right away with **Check for updates**
   and **Update all** in the tray app.

A manual run of the workflow (Actions → Release → Run workflow) only builds the package and keeps it as an artifact.

Installed copies read `https://github.com/rotate4all/syscom/releases/latest/download/feed.json`. Change
`UpdateFeedUrl` in `%ProgramData%\SYSCOM\config.json` to host releases elsewhere. Note that `releases/latest`
download links only work while the repository is **public**. For a private repository, host `feed.json` and the
zips somewhere reachable and point `UpdateFeedUrl` at it.

### feed.json

```json
{
  "syscom": { "version": "0.2.0", "url": "https://github.com/rotate4all/syscom/releases/download/v0.2.0/syscom-app-0.2.0.zip", "sha256": "…" },
  "web":    { "version": "0.2.0", "url": "https://github.com/rotate4all/syscom/releases/download/v0.2.0/syscom-web-0.2.0.zip", "sha256": "…" }
}
```

## Settings (`%ProgramData%\SYSCOM\config.json`)

| Key | Default | Meaning |
| --- | --- | --- |
| `UpdateFeedUrl` | GitHub latest release `feed.json` | where SYSCOM/web updates are announced; empty disables them |
| `AutoCheckUpdates` | `true` | check for updates every `UpdateCheckHours` |
| `UpdateCheckHours` | `6` | hours between checks (1 to 168) |
| `AutoUpdate` | `syscom` | what is installed automatically: `syscom` (SYSCOM and dashboard), `all`, or `off` (notify only) |
| `TokenLifetimeDays` | `30` | how long a sign-in lasts after its last use (open dashboards renew it daily) |
| `MinPawnIoVersion` | `2.2.0` | older PawnIO is flagged as a security error |
| `PawnIoInstallArgs` | `-install -silent` | arguments for the PawnIO setup |
| `MinIntervalMs` | `500` | fastest polling a browser may request |
| `HistoryEnabled` | `true` | record every sensor for 30 days (`history\` next to `config.json`); keeps sensors polled while nobody watches |
| `HistorySampleMs` | `2000` | how often sensors are read for the history while nobody watches |
| `Port` / `DiscoveryPort` | `47810` / `47811` | |
| `HttpsEnabled` / `HttpsPort` | `true` / `47812` | HTTPS with this PC's own certificate (needed to install the phone app) |
| `DiscoveryEnabled` | `true` | LAN broadcasts |
