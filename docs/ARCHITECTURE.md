# Architecture

SYSCOM is a server-only system. Every monitored PC runs the same server; browsers are the clients.

```
 phone / tablet / PC browser                         Windows PC (one per monitored machine)
┌───────────────────────────┐   HTTP + WebSocket    ┌──────────────────────────────────────────────┐
│ web dashboard (web/)      │◀────────────────────▶ │ syscom.exe  (Windows service "SYSCOM")       │
│  • one page per PC        │  :47810 / :47812 TLS  │  ├─ SensorHub ── LibreHardwareMonitorLib ─┐  │
│  • swipe between PCs      │                       │  │             └─ DriveSpaceSource (.NET) │  │
│  • PC list (kept on PC)   │                       │  ├─ Auth (password → signed token)        │  │
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
| `SensorHub` | `Sensors/SensorHub.cs` | One shared polling loop for all clients. It runs at the fastest interval any connected client asked for (never below `MinIntervalMs`, 500 by default). With nobody connected it samples every `HistorySampleMs` (2 s) for the history, or **stops touching hardware** when `HistoryEnabled` is off. |
| `HistoryStore` | `History/HistoryStore.cs` | 30 days of every sensor: readings averaged into 1-minute buckets (kept 3 days) and 10-minute buckets (kept 31 days), with each bucket's lowest and highest reading. A closed bucket is one fixed-size record appended to the file for its tier and day, so writing is one sequential append a minute and reading a sensor is a seek per record. About 75 MB for 600 sensors. Queries return at most 1500 evenly spaced points. |
| `DashboardStore` | `Dashboards/DashboardStore.cs` | This PC's dashboard (`dashboard.json`): desktop and phone layouts, appearance, saved layouts. Shared by every browser that signs in. Each save bumps a revision; open WebSockets are told (`layout` message) and reload it. |
| `BackgroundStore` | `Dashboards/BackgroundStore.cs` | The dashboard's background picture (`background.<ext>`, 8 MB max, checked to be an image); its id (a hash) goes in the dashboard's `look`. |
| `DeviceListStore` | `Dashboards/DeviceListStore.cs` | The list of PCs for the dashboards this PC serves (`devices.json`), so every address of this PC and every browser finds the same PCs. Revisioned like the dashboard (`devices` message). |
| `PeerRelay` | `Relay/PeerRelay.cs` | `/relay/{pc}/…`: forwards SYSCOM's own endpoints (HTTP and WebSocket) to another SYSCOM PC on the LAN, so an HTTPS dashboard reaches PCs whose certificate the browser doesn't trust (see [API.md](API.md#relay-relaypc)). |
| `LhmSensorSource` | `Sensors/LhmSensorSource.cs` | Wraps LibreHardwareMonitorLib: CPU, GPU, memory, motherboard, storage, network, battery, PSU. Sensor IDs are LHM's stable identifiers (e.g. `/amdcpu/0/temperature/2`). LHM adds some sensors only after reading them and drops ones that stop reporting, so the catalog follows its sensor added/removed events (batched to one update per 3 s). |
| `DriveSpaceSource` | `Sensors/DriveSpaceSource.cs` | Free/used/total space per fixed drive via `DriveInfo` (no dependency). |
| `DerivedSensors` | `Sensors/DerivedSensors.cs` | Readings worked out from others: percent used for every "X Used" / "X Total" pair on one device (id = the used reading's id + `/percent`). Gives VRAM % on every GPU vendor; LibreHardwareMonitor's own "GPU Memory" load means VRAM on NVIDIA but memory controller activity on AMD. Windows' "D3D Dedicated Memory Used" (Task Manager's figure) has no total of its own, so it gets a Total (the driver's "GPU Memory Total"), a Free and a percent (`/total`, `/free`, `/percent`). Derived readings are listed right after their source. |
| `PresentMonitorSource` | `Sensors/PresentMonitorSource.cs` | Frame rate and frame time for any GPU from Windows' present events (real-time ETW session on the DXGI and D3D9 providers, called directly, no dependency). Picks the app drawing the most frames, ignoring the compositor and shell. Native frame rate (without generated frames) from NVIDIA's PC Latency Stats provider (Reflex frame markers, TraceLogging) and the Intel-PresentMon provider's frame types (XeSS-FG rendered presents; AMD AFMF frames, added to the shown rate). Runs while the hub polls (always while history is on). |
| `MockSensorSource` | `Sensors/MockSensorSource.cs` | Synthetic data for client development (`--mock`, default on non-Windows builds). |
| `MetricsSession` | `Streaming/MetricsSession.cs` | One WebSocket client. Sends the sensor catalog, then compact value arrays for just the sensors the client's dashboard shows. A dashboard whose cards refresh at different rates subscribes one stream per rate (`streams`); each stream gets its own frames, tagged with its id. |
| `ApiKeyStore` | `Api/ApiKeyStore.cs` | API keys (`apikeys.json`): name, access (read or full), created and last used. Only a SHA-256 hash of each key is stored; the key itself is shown once, when it's made. |
| `ApiAccess`, `PublicApi`, `Readings` | `Api/` | Who may call what (a signed-in dashboard, a read key or a full-access key), and the endpoints for scripts: sensors with filters, categories, values, and cards, sections and whole dashboards with their readings in the units the cards show ([API.md](API.md)). A call with no dashboard open asks the hub for a fresh reading, which keeps it polling every second for a minute after the last call. |
| `InstallEntry` | `Components/InstallEntry.cs` | Keeps the version shown in Windows' Installed apps (the entry the setup made) in step after in-app updates. |
| `AuthService` | `Auth/AuthService.cs` | PBKDF2-SHA256 password hash, HMAC-SHA256 signed bearer tokens (30 days, renewed daily by open dashboards through `/api/token`), per-IP lockout. Changing the password rotates the signing key, which signs out every browser. |
| `LanDiscovery` | `Discovery/LanDiscovery.cs` | Broadcasts `{app:"syscom", id, name, port}` every 5 s on UDP 47811 and lists peers it hears. Browsers can't do UDP, so the dashboard asks any connected server for `/api/peers`. |
| `ComponentManager` | `Components/` | Health, version checks and updates for each component, and automatic installs (`AutoUpdate`; see [UPDATES.md](UPDATES.md)). |
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
browsers watching cost the same hardware reads as one. Each client still gets its own send interval (one per card
refresh rate it uses), and the values come from the latest snapshot. The loop runs at the fastest rate anyone asks
for, so a single card at 500 ms makes the PC read every 500 ms while the other cards' frames still go out at their
own pace.

## Web client (`web/`)

Plain ES modules, no framework and no build step. About 40 KB uncompressed.

| File | Role |
| --- | --- |
| `js/app.js` | Pages (one per PC) with sections, swipe tracking, edit mode, phone preview, layout sync with the PC, dialogs, settings, history dialog |
| `js/connection.js` | Login and daily token renewal, WebSocket stream, reconnect with backoff, `4401` close code means "sign in again"; direct or relayed route to each PC; dashboard, background, device list and history API calls |
| `js/widgets.js` | Cards: gauges (SVG, five styles), horizontal and vertical bars (CSS), graph (canvas), value; device icons; value and unit positions, value sizes; threshold colouring; smooth motion (one shared animation loop for numbers, CSS transitions, gliding graphs); suggested layout |
| `js/dnd.js` | Edit mode drag and drop (cards and whole sections) and corner resizing, with pointer events (mouse, pen and touch) |
| `js/history.js` | History chart (canvas): average line, lowest-to-highest band, thresholds, crosshair tooltip |
| `js/store.js` | Device list, theme and a cache of each PC's dashboard in `localStorage`; layout and appearance normalising |
| `js/transfer.js` | Layout files: export one layout of one PC, import it into any PC by matching its readings to that PC's sensors |
| `sw.js`, `manifest.webmanifest` | App shell cache and install metadata (PWA) |
| `trust.html` | Phone setup page served at `/trust` |

Swiping is native CSS scroll-snap, so there's no gesture library. Only the visible page repaints; hidden pages keep
graph history. Streams keep running while the tab is in the background (nothing is painted until it's visible
again), so readings are current the moment you look. A new catalog only rebuilds the page when a widget's sensor
appeared or disappeared; otherwise names update in place and graph history stays. Servers go idle once every
browser has closed the dashboard.

### Grid and sizes

Cards sit on a grid of fixed-height rows (56 px) and columns about 90 px wide: 4 on a phone, more on wider screens (up
to 16). A card's size is a number of columns and rows, so a layout keeps its shape from phone to desktop; a card never
spans more columns than the screen has. Everything inside a card is sized in container query units, so it scales
with the card. Bar, arc and line thickness work the same way; a card's optional `thick` (25 to 300, a percentage)
multiplies that proportional thickness, so a thicker card still scales when it is resized. A card can carry up to
two more readings (`extra: [{ sensor, show }]`, `show` = `value`, `both` or `graph`) drawn under its main display,
and its own title (`label`) and subtitle (`sub`, `hideSub`). While the widget editor is open, the browser subscribes
to every sensor (a subscribe message without a list) so the sensor list can show live readings. Sections are separate grids, so cards never flow from one section into another.

### Where the dashboard lives

Each PC's layouts are saved on that PC (`PUT /api/dashboard` with `{ widgets, phone, look, saved, current }`) and
cached in the browser,
so they show instantly and work offline. `widgets` is the desktop layout; `phone` is the phone layout, shown on
screens narrower than 700 px and in the phone preview. While `phone` is null, phones show the desktop layout; the
first change made on a phone (or in the preview) turns a copy of it into the phone layout, and "Use the desktop
layout" in edit mode sets it back to null. Graph history is kept per page, so rebuilt cards keep their graphs. Edits made while the PC is unreachable are kept and sent on the next connection; otherwise the PC's
copy wins. The first browser to connect to a PC without a saved layout uploads its own. `look` is the PC's
appearance (background, card opacity, rounding, animations), `saved` its named layouts and `current` which saved
layout each of the two layouts was loaded from. Undo and redo keep the last 5 states of the layout on screen, in
the page only.

### The list of PCs

Browsers keep data per origin, so `http://localhost:47810`, `http://<ip>:47810` and `https://<ip>:47812` of one PC
each start empty. The PC that served the page (the "home" PC, found by comparing `/api/info` ids) therefore keeps
the list too (`/api/devices`): the first time a browser meets it, both lists are merged; after that the newest change
wins, announced over the WebSocket like the dashboard. Sign-ins and the theme stay in the browser.

### Reaching other PCs from an HTTPS dashboard

Mixed content rules make an HTTPS page talk HTTPS to every PC, and each PC has its own certificate authority. The
dashboard tries a PC directly (works once its CA is trusted) and otherwise goes through the home PC's relay
(`/relay/{id}/…`), which forwards to that PC's HTTP port on the LAN. The choice is made again on every reconnect.

## Tray app (`src/Syscom.Tray`)

A WinForms app that ships with .NET, so it adds no dependency. Installed to start at logon with administrator
rights (it needs them to read the admin key and to manage the service and drivers). The icon colour shows overall
health; the icon can also be drawn transparent, in colour or grayscale, like modern tray icons (a per-user setting
in `%LocalAppData%\SYSCOM\tray.json`). Its window shows server info, the sign-in password state, the automatic
update choice, and the component table with Check / Update selected / Update all. **Set up phone…** shows the QR
code for the phone setup page.

One copy runs at a time (`Global\SYSCOM.Tray` mutex); starting it again (Start menu → SYSCOM, which passes
`--open`) signals the running copy to show its window (`Global\SYSCOM.Tray.Show` event). While an update or restart
runs, its buttons stay disabled until the updater's result arrives. When an update has replaced its files and the
server is back on that version, it starts the new copy (`--restarted`, which waits for the old one to exit) and
exits. It also creates the Start menu entries when they are missing and deletes its own `*.syscom-old` leftovers.

At sign-in it often starts before Explorer has made the taskbar. Its icon then comes back when Explorer announces
the taskbar ("TaskbarCreated", which an elevated app must let through: `Taskbar.cs`), and if that message never
arrives, when it sees a new taskbar window on its next refresh. If the service is set to start automatically but is
stopped when the tray app starts, the tray app starts it.

## Starting with Windows

The service starts automatically, and Windows waits only 30 s (by default) for a service to report that it started,
and doesn't retry if it doesn't (events 7009 and 7000). So `syscom.exe` reports to Windows before it touches the hardware: LibreHardwareMonitor finds
the devices (which can take long while Windows starts: every drive's health data is read) on the sensor loop's first
read, while the web server already answers; browsers get the sensors when it's done. The data folder's permissions
are only reset (every file, recursively) when the folder itself isn't locked down yet. Its recovery settings restart
it 5, 10 and 30 s after a crash, or after it stopped because a part of it failed (it then reports an error exit
code).

## Updater (`src/Syscom.Updater`)

A small console app the server launches, from a copy in `%ProgramData%\SYSCOM\updates\<run>\updater`, so it can
replace its own files (the new version's copy when SYSCOM itself is updated). It stops the service, backs up and
swaps files, starts the service, waits for `/api/health?ready=true` (sensors read), and rolls back on failure. A file another program has open
(the running tray app's) is renamed to `*.syscom-old` and replaced; the server deletes those leftovers when it
starts. It writes `last-result.json`, which the server re-reads when it changes and the tray app shows.

## Install layout

```
C:\Program Files\SYSCOM\
  syscom.exe, syscom-tray.exe, syscom-updater.exe (+ .dll, .runtimeconfig.json)
  LibreHardwareMonitorLib.dll and its dependencies
  runtime\        private .NET runtime (NETCore + ASP.NET Core + WindowsDesktop); exes find it via AppHostRelativeDotNet
  web\            dashboard files (version.json inside)
  components.json versions bundled at build time
  unins000.exe    the setup's uninstaller (installs made with install.ps1 have uninstall.ps1 and drivers\ instead)
C:\ProgramData\SYSCOM\          (SYSTEM + Administrators only)
  config.json     name, ports, password hash, token secret, update feed URL
  admin.key       shared secret for the tray app
  apikeys.json    API keys (hashes only)
  dashboard.json, devices.json, history\   dashboards, the list of PCs, 30-day sensor history
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
`install.ps1 -Port … -DiscoveryPort … -HttpsPort …` (the setup uses the defaults, and keeps the ports of an existing
`config.json`).
