# Architecture

SYSCOM is a server-only system. Every monitored PC runs the same server; browsers are the clients.

```
 phone / tablet / PC browser                         Windows PC (one per monitored machine)
┌───────────────────────────┐   HTTP + WebSocket    ┌──────────────────────────────────────────────┐
│ web dashboard (web/)      │◀────────────────────▶ │ syscom.exe  (Windows service "SYSCOM")       │
│  • one page per PC        │  :47810 / :47812 TLS  │  ├─ SensorHub ── LibreHardwareMonitorLib ─┐  │
│  • swipe between PCs      │                       │  │             └─ DriveSpaceSource (.NET) │  │
│  • layouts in localStorage│                       │  ├─ Auth (password → signed token)        │  │
│  • installable (PWA)      │                       │  ├─ TlsManager (own CA, HTTPS certificate) │  │
└───────────────────────────┘                       │  ├─ LanDiscovery (UDP :47811 broadcast)    │  │
                                                    │  ├─ ComponentManager (health, updates)     │  │
                                                    │  └─ Web client (web\ folder or embedded)   │  │
                                                    │                                ▲         PawnIO driver
                                                    │  syscom-tray.exe ──loopback────┘ (admin API)  │
                                                    │  syscom-updater.exe (runs during updates)     │
                                                    └──────────────────────────────────────────────┘
```

## Server (`src/Syscom.Server`)

One .NET 10 executable built on ASP.NET Core's Kestrel, with no MVC and no static-file middleware. It runs as a
Windows service, or as a console app for development.

| Piece | File | Role |
| --- | --- | --- |
| `SensorHub` | `Sensors/SensorHub.cs` | One shared polling loop for all clients. It runs at the fastest interval any connected client asked for (never below `MinIntervalMs`, 500 by default) and **stops touching hardware when nobody is connected**. |
| `LhmSensorSource` | `Sensors/LhmSensorSource.cs` | Wraps LibreHardwareMonitorLib: CPU, GPU, memory, motherboard, storage, network, battery, PSU. Sensor IDs are LHM's stable identifiers (e.g. `/amdcpu/0/temperature/2`). |
| `DriveSpaceSource` | `Sensors/DriveSpaceSource.cs` | Free/used/total space per fixed drive via `DriveInfo` (no dependency). |
| `MockSensorSource` | `Sensors/MockSensorSource.cs` | Synthetic data for client development (`--mock`, default on non-Windows builds). |
| `MetricsSession` | `Streaming/MetricsSession.cs` | One WebSocket client. Sends the sensor catalog, then compact value arrays for just the sensors the client's dashboard shows. |
| `AuthService` | `Auth/AuthService.cs` | PBKDF2-SHA256 password hash, HMAC-SHA256 signed bearer tokens (30 days), per-IP lockout. Changing the password rotates the signing key, which signs out every browser. |
| `LanDiscovery` | `Discovery/LanDiscovery.cs` | Broadcasts `{app:"syscom", id, name, port}` every 5 s on UDP 47811 and lists peers it hears. Browsers can't do UDP, so the dashboard asks any connected server for `/api/peers`. |
| `ComponentManager` | `Components/` | Health, version checks and updates for each component (see [UPDATES.md](UPDATES.md)). |
| `AdminAccess` | `Admin/AdminAccess.cs` | Guards `/api/admin/*`: loopback only, plus the key in `%ProgramData%\SYSCOM\admin.key` (folder ACL: SYSTEM and Administrators). |
| `WebClient` | `Web/WebClient.cs` | Serves `web\` next to the exe, falling back to a copy embedded in the exe. |
| `TlsManager` | `Tls/TlsManager.cs` | Optional HTTPS. Creates this PC's own certificate authority once (20 years, name-constrained to private addresses, `*.local` and the PC's name) and a server certificate for the current LAN addresses (397 days). Re-checks every 5 minutes and re-issues the server certificate when addresses change or expiry nears, so phones only trust the CA once. |

### HTTPS and the phone app

Browsers only install web apps (and only run service workers) on secure origins. A home PC has no public domain,
so there's no public certificate to get. Instead each PC is its own tiny CA, and a phone trusts it once through
`/trust`: an iOS configuration profile or a DER certificate, plus per-platform steps. The tray app's
**Set up phone…** window shows a QR code for that page (a built-in encoder, `src/Syscom.Tray/QrCode.cs`) and the
CA fingerprint to compare.

When the dashboard itself is opened over HTTPS, it talks to every PC on that PC's HTTPS port (`httpsPort` from
`/api/info`, or `httpsUrl` from `/api/peers`), because a secure page can't open plain `ws://` connections.

### Why one polling loop?

Reading hardware (especially SMART and Super I/O) costs far more than serialising a few numbers. With one loop, ten
browsers watching cost the same hardware reads as one. Each client still gets its own send interval, and the
values come from the latest snapshot.

## Web client (`web/`)

Plain ES modules, no framework and no build step. About 40 KB uncompressed.

| File | Role |
| --- | --- |
| `js/app.js` | Pages (one per PC), swipe tracking, edit mode, dialogs, settings |
| `js/connection.js` | Login, WebSocket stream, reconnect with backoff, `4401` close code means "sign in again" |
| `js/widgets.js` | Gauge (SVG), bar (CSS), graph (canvas), value; threshold colouring; suggested layout |
| `js/store.js` | Devices, dashboards and theme in `localStorage`; export/import |
| `sw.js`, `manifest.webmanifest` | App shell cache and install metadata (PWA) |
| `trust.html` | Phone setup page served at `/trust` |

Swiping is native CSS scroll-snap, so there's no gesture library. Only the visible page repaints; hidden pages keep
graph history. When the tab or phone screen is hidden, all streams close, so the servers go idle.

Each browser keeps its own layouts (localStorage is per origin). Open the dashboard from one "home" PC address,
and add the other PCs from there.

## Tray app (`src/Syscom.Tray`)

A WinForms app that ships with .NET, so it adds no dependency. Installed to start at logon with administrator
rights (it needs them to read the admin key and to manage the service and drivers). The icon colour shows overall
health. Its window shows server info, the sign-in password state, and the component table with Check / Update
selected / Update all. **Set up phone…** shows the QR code for the phone setup page.

## Updater (`src/Syscom.Updater`)

A small console app the server launches, from a copy in `%ProgramData%\SYSCOM\updates\<run>\updater`, so it can
replace its own files. It stops the service, backs up and swaps files, starts the service, waits for
`/api/health`, and rolls back on failure. It writes `last-result.json`, which the tray app shows.

## Install layout

```
C:\Program Files\SYSCOM\
  syscom.exe, syscom-tray.exe, syscom-updater.exe (+ .dll, .runtimeconfig.json)
  LibreHardwareMonitorLib.dll and its dependencies
  runtime\        private .NET runtime (NETCore + ASP.NET Core + WindowsDesktop); exes find it via AppHostRelativeDotNet
  web\            dashboard files (version.json inside)
  drivers\        PawnIO_setup.exe used by the installer
  components.json versions bundled at build time
C:\ProgramData\SYSCOM\          (SYSTEM + Administrators only)
  config.json     name, ports, password hash, token secret, update feed URL
  admin.key       shared secret for the tray app
  tls\            ca.pfx / ca.crt (this PC's CA), server.pfx (current HTTPS certificate)
  updates\        staging, backups, updater logs, last-result.json
```

No `deps.json` files are generated (`GenerateDependencyFile=false`). The host loads whatever DLLs are in the
folder, so the updater can drop in a newer LibreHardwareMonitor and its dependencies.

## Ports

| Port | Protocol | Purpose |
| --- | --- | --- |
| 47810 | TCP | Dashboard, REST API, WebSocket |
| 47811 | UDP | LAN discovery broadcasts |
| 47812 | TCP | Dashboard, REST API, WebSocket over HTTPS (optional, `HttpsEnabled`) |

All can be changed in `config.json` (`Port`, `DiscoveryPort`, `HttpsPort`) or with
`install.ps1 -Port … -DiscoveryPort … -HttpsPort …`.
