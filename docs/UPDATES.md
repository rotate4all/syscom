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
The server checks every source once a day (`AutoCheckUpdates`). It **never installs anything on its own**.

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
   run folder, writes `plan.json` and starts it.
4. The updater stops the **SYSCOM** service, backs up every file it will overwrite into `backup\`, copies the
   new files, starts the service and polls `http://127.0.0.1:<port>/api/health` for up to 60 s. If the service
   stops on its own, it fails fast instead of waiting.
5. **Not healthy?** It stops the service, restores the backup, deletes files that were new, and starts the old
   version again.
6. The outcome goes to `updates\last-result.json` and `updates\<timestamp>\updater.log`. The tray app shows it.

Updating LibreHardwareMonitor in place works because the install has no `deps.json` pinning. If a new LHM version
breaks API compatibility with the SYSCOM build, the health check fails and the update rolls back. A new SYSCOM
release built against that LHM version fixes it.

## Building a package

`versions.json` pins what the full package contains:

```json
{
  "syscom": "0.1.0",
  "web": "0.1.0",
  "dotnet": { "channel": "10.0", "runtime": "latest" },
  "librehardwaremonitor": "0.9.6",
  "pawnio": "latest",
  "windowsServices": "10.0.14"
}
```

Bump a value and run `pwsh build\package.ps1`. The script builds SYSCOM against the pinned LHM version, downloads
and verifies the runtime and PawnIO, and writes to `dist\`:

- `SYSCOM-<ver>-win-x64.zip`: the full package (extract, then run `install.ps1`)
- `syscom-app-<ver>.zip`, `syscom-web-<webver>.zip`: update assets
- `feed.json`: the feed installed copies read

## Cutting a release

1. Update `versions.json` (`syscom`, and `web` if the dashboard changed) and `CHANGELOG.md`.
2. Merge to `main`, then tag: `git tag v0.2.0 && git push origin v0.2.0`.
3. The **Release** workflow builds the package on Windows and publishes a GitHub release with all files from `dist\`.

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
| `AutoCheckUpdates` | `true` | daily check (never installs) |
| `MinPawnIoVersion` | `2.2.0` | older PawnIO is flagged as a security error |
| `PawnIoInstallArgs` | `-install -silent` | arguments for the PawnIO setup |
| `MinIntervalMs` | `500` | fastest polling a browser may request |
| `HistoryEnabled` | `true` | record every sensor for 30 days (`history\` next to `config.json`); keeps sensors polled while nobody watches |
| `HistorySampleMs` | `2000` | how often sensors are read for the history while nobody watches |
| `Port` / `DiscoveryPort` | `47810` / `47811` | |
| `HttpsEnabled` / `HttpsPort` | `true` / `47812` | HTTPS with this PC's own certificate (needed to install the phone app) |
| `DiscoveryEnabled` | `true` | LAN broadcasts |
