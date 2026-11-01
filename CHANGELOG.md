# Changelog

All notable changes are listed here. Versions follow [semantic versioning](https://semver.org/): a new feature
request bumps the minor version (0.x.0), a fix the patch version (0.x.y).

Versions up to 0.16.2 were not tagged one by one: they all shipped in the first release (pull requests #1 and #2,
31 October 2026). Their entries below group those changes by request, in the order they were asked for, so the
version number counts the 16 feature requests and 2 fixes since the first run. 0.17.0 is the first version published
as a GitHub release, and installed PCs update to it from there.

## [0.18.2] - 2026-11-01

### Fixed
- **The tray icon didn't appear after Windows restarted**, although the tray app was running. It starts as you sign
  in, often a few seconds before Windows has made the taskbar, and an icon added before then is lost. Apps add their
  icon again when Explorer announces the new taskbar, but Windows doesn't pass that announcement on to an app running
  as administrator, which the tray app is. It now lets it through, and also adds its icon again when it sees a new
  taskbar on its next refresh (this also covers Explorer restarting).
- **The SYSCOM service didn't start with Windows on some PCs**, and the tray app said SYSCOM is not running. The
  service read all the hardware, every drive's health data included, before telling Windows it had started. While
  Windows starts (with backup software starting too, say), that can take longer than Windows waits for a service
  (30 s, on some PCs 45 s), and then Windows gives up and doesn't try again: events 7009 and 7000 in the System log.
  SYSCOM now tells Windows at once and finds the hardware afterwards; the dashboard answers meanwhile and gets the
  sensors as soon as they're found. The Windows event log (Application) says how long that took: "Found … sensors
  in … s". It also no longer resets the permissions of every file in its data folder at every start (thousands of
  files with the update backups), only when the folder isn't locked down yet.
- When the tray app starts (as you sign in) and the service is stopped, it starts it, unless the service was set to
  start manually or disabled. **Restart server** in the tray menu starts a stopped service too.
- Windows now restarts SYSCOM also when it stops with an error, not only when it crashes, and SYSCOM reports a stop
  caused by a failing part as an error. The setup (and `install.ps1`) sets this up; in-app updates don't change
  service settings, so run the 0.18.2 setup once to get it.

### Changed
- An in-app update counts as successful once the new version has read the sensors (`/api/health?ready=true`), as
  before, when SYSCOM read them before answering at all. A new sensor library that hangs is still rolled back.

## [0.18.1] - 2026-11-01

### Fixed
- **The setup ended with "Unable to execute file … syscom-tray.exe, CreateProcess failed; code 740"** when *Open
  SYSCOM* was ticked on its last page. The tray app needs administrator rights, and the setup started it without
  them. It now starts it with the setup's own rights. Nothing else was affected: SYSCOM was installed, and the tray
  app was already running from its sign-in task. CI now checks that the setup starts the tray app this way.

## [0.18.0] - 2026-11-01

### Added
- **A Windows setup: `SYSCOM-Setup-<version>.exe`.** Download it, run it, click through: no zip to extract, no
  PowerShell script, no "Run with PowerShell" blocked by Windows because the file came from the internet. It installs
  SYSCOM with its own .NET runtime, the PawnIO driver when needed, the service, the firewall rules and the tray app,
  lists SYSCOM in Settings → Apps → Installed apps with an uninstaller (which asks whether to keep your settings and
  history), and upgrades an existing SYSCOM in place, including one installed with `install.ps1`. Made with Inno
  Setup 7.1.1. It isn't code-signed yet, so Windows may say it protected your PC: **More info → Run anyway**. The
  zip with `install.ps1` stays available for scripted installs.
- **Each card can refresh at its own rate**: every 500 ms to every 60 s, or (the default) at the dashboard's rate.
  Card editor → *Refresh*. Cards on different rates share one connection; the PC reads its sensors at the fastest
  rate any card asks for. PCs still on an older version keep using the dashboard's rate.
- **An API with keys.** Create and revoke keys in the dashboard (Settings → *API keys and reference…*): a read key
  reads everything the dashboard shows, a full-access key can also change the dashboard and the list of PCs. Every
  sensor with its value (filter by category, kind of reading, hardware or name), sensors by category, the history,
  and cards, sections and whole dashboards with their readings as the cards show them (unit, text, colour level).
  The live WebSocket stream takes keys too.
- **An API button on every card, section and dashboard** (`</>` in edit mode, and *API…* in the dashboard's tools).
  It shows the exact address and ready-to-run examples (curl, PowerShell, Python, JavaScript, browser) for that card,
  filled in with the PC's address and your key, and *Try it* shows the live answer. The window also has the keys and
  the full reference, with examples built from the PC's own sensors.
- **API reference on GitHub**: [docs/API.md](docs/API.md) is generated from the same source as the in-app one, and
  CI fails if it falls behind.
- **Benchmark**: `build\benchmark.ps1` measures SYSCOM's CPU and memory idle and with a dashboard streaming every
  sensor at 1 s and 500 ms, and other monitoring apps side by side with `-Compare`. CI runs it on every change,
  against LibreHardwareMonitor's own app. The README shows the results.
- **CI installs the setup** on Windows, checks the service, tray task, firewall rules, Start menu, Installed apps
  entry and the API, upgrades over it and uninstalls it, checking nothing is left behind.

### Changed
- The README shows what the dashboard can do now: card types and gauge styles, per-card options, layouts, looks, the
  API and how light SYSCOM is, with new screenshots. The roadmap includes a Linux release.

### Fixed
- **Animations didn't play in desktop browsers** (they did on phones). Windows with *Animation effects* turned off
  (Settings → Accessibility → Visual effects, off on many PCs) tells browsers to reduce motion, and the dashboard
  followed it. Appearance → *Animations* now has **Smooth** (the default: always animate), **Follow system** (the old
  behaviour) and **Off**.

## [0.17.2] - 2026-11-01

### Fixed
- **SYSCOM took 30 seconds to stop, then crashed, whenever a dashboard was open** (a phone on the wall, a browser
  tab). Each open dashboard kept the server waiting until Windows' time limit ran out. Open dashboards (direct and
  relayed) are now told right away that SYSCOM is restarting, and reconnect when it's back; stopping takes a moment.
  A stop that still runs out of time ends quietly instead of crashing.
- **install.ps1 failed halfway with "file is being used by another process"** and closed its window. Windows reports
  the service as stopped before its process has exited (a crashing one lingers while its crash dump is written), so
  the installer started copying too early. The installer (and the updater, and uninstall.ps1) now wait for the process
  itself to exit, stop anything else still running from the install folder, and retry a locked file a few times.
- **The installer's window closed before you could read it.** When it restarts itself as administrator, the window
  now stays open at the end, showing the addresses and password or what went wrong. Everything it shows is also
  written to `%ProgramData%\SYSCOM\install.log`.

## [0.17.1] - 2026-11-01

### Fixed
- **Updating SYSCOM from the tray app failed and rolled back while the tray app was running.** The update also
  replaces the tray app's own files, and Windows doesn't let a running program's files be overwritten. The updater
  now renames those files aside and puts the new ones in their place; the old copies are deleted later. The tray app
  then restarts itself into the new version (with its window open, if it was).
- **The tray window kept showing the old version and "Installing updates…"**, so it looked like nothing happened.
  The outcome of an update is now read as soon as the updater writes it, not only when SYSCOM starts.
- **Update buttons could be clicked again while an update ran.** Check for updates, Update selected, Update all and
  Restart server are disabled, with a progress bar, from the click until SYSCOM is back and the result shows
  (in red when the update failed or was rolled back). A second request while SYSCOM restarts is refused.
- **"The SYSCOM service is stopped. Start it now?" popped up during an update** (and again every few seconds when
  answered No). It's not asked while an update restarts SYSCOM, and only once otherwise.
- A failed or rolled-back version could be installed automatically again; it's now skipped until a newer one ships.
- Updates that download a lot (the .NET runtime) no longer time out in the tray app after two minutes.

### Added
- **Start menu entries**: *SYSCOM* opens the tray app's window (and starts the tray app if it was closed), *SYSCOM
  dashboard* opens the dashboard. Opening SYSCOM while it already runs brings its window up. Installs that update
  in place get them the first time the new tray app starts.
- The tray app has the SYSCOM icon in Explorer and the Start menu.
- When SYSCOM itself is updated, the new version's updater installs it, so updater fixes apply to that same update.
- Old update folders are pruned (the 5 newest are kept).

## [0.17.0] - 2026-11-01

### Added
- **Saved layouts.** Save the layout on screen under a name and switch between saved layouts with one click (the
  layers button in the top bar, or Settings → Layouts…). Desktop and phone layouts are saved separately, on the PC.
  The saved layout in use shows next to the PC's name, with a • once it's been changed; Save changes, Replace,
  Rename, Export and Delete are on each entry.
- **Export and import per PC and per layout** (desktop or phone). An export file names the readings it uses, so
  importing it on another PC matches each reading to that PC's sensors: the same sensor, else the same kind of
  reading on the same kind of hardware with the same name (CPU Total, GPU Core, …), else the closest name. Drive
  letters only match the same letter. The result lists readings that weren't found; those cards stay, marked
  unavailable, until another sensor is picked. Old backup files still import.
- **Undo and redo** of the last 5 dashboard edits (buttons in edit mode, Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z).
- **Dashboard background** per PC: the theme's, a colour, or a picture (fill, show whole or tile, with a fade toward
  the theme colour so readings stay legible). Pictures are stored on the PC (`/api/dashboard/background`), scaled
  to at most 2560 px first, so every browser and phone sees the same background.
- **Card transparency**: card background opacity for the whole dashboard (Appearance) and per card (widget editor).
- **Corner rounding** for cards and bars, from square to fully rounded, for the dashboard and per card.
- **Smooth animations**: gauges, needles and bars glide to each new reading, numbers count to it and graphs scroll
  steadily instead of jumping, over most of one polling interval. Can be turned off (Appearance), and stays off
  when the system asks for reduced motion.
- **Full screen**: the button in the top bar (or F) hides the bar with the PC's name and buttons, and the browser's
  bars where it allows; Esc or the corner button leaves it. The PC's name shows for a moment when swiping.
- **Unit (suffix) position for every card type**: right, left, above, below or hidden, for gauges of every style
  (segments and ring included), bars and graphs, not only big numbers.
- **Value size for every card type**, gauges included (extra small to extra large), like the extra readings' size.
- **Bars: show or hide the min and max values.** Hidden, a vertical bar keeps its width and stays centered in the
  card, and a horizontal bar uses the room.
- **500 ms polling** in Settings (1 s stays the default).
- **Automatic updates.** Each PC checks this repository's releases every 6 hours and installs new versions of
  SYSCOM and its dashboard by itself (default), or of every component, or only notifies (tray app → Install
  updates automatically). An update that restarts SYSCOM waits until no dashboard is open, for up to a day. A version that failed
  and was rolled back is not tried again. Open dashboards reload themselves once the PC they were opened from has
  a newer dashboard.
- **Releases on merge**: merging a version bump to `main` builds the package and publishes the GitHub release
  (`release.yml`), which is what installed PCs update from.
- **Tray icon styles**: Colour (default), Transparent, or Transparent grayscale like modern tray icons, which
  follows a light or dark taskbar. In the tray menu (Tray icon) and the main window.
- Server: `POST /api/token` (renew a sign-in), `GET`/`PUT /api/devices` (the device list), the dashboard
  background endpoints, `/relay/{pc}/…` (see below), and `GET`/`POST /api/admin/settings` for the tray app.

### Changed
- **Sign-ins last 30 days after their last use** instead of 30 days after signing in: an open dashboard renews its
  sign-in once a day.
- **The device list is kept on the PC that serves the dashboard**, as well as in the browser. Opening the dashboard
  on another address of the same PC (localhost, its IP, https), or in another browser, brings every added PC back;
  each one still asks for its own password. Adding or removing a PC anywhere updates the others.
- **Adding a PC from the HTTPS dashboard (the phone app) just works.** When the browser doesn't trust that PC's
  certificate (each PC has its own), the PC that served the dashboard relays to it on the LAN (`/relay/…`), so
  only that first PC's certificate is needed. Only SYSCOM's sign-in, readings and dashboard endpoints are relayed
  (never the management API), only to SYSCOM servers on the local network, and every PC still checks its own
  password.
- A PC's dashboard on the server now also holds its appearance and saved layouts (`look`, `saved`, `current`).
- Rebuilt cards (after an edit, undo, or switching layouts) show the latest readings at once instead of a blank for
  up to one polling interval.

### Fixed
- A PC added the evening before was gone after signing in again on the desktop. Browsers keep a page's data per
  address, and the tray app opens `http://localhost:47810` while other links use the PC's IP, so each address had
  its own device list. The list now lives on the PC (see Changed).
- Importing a layout exported on another PC added nothing: it only looked for the exporting PC's own entry.
- Adding a discovered PC from the HTTPS dashboard failed until that PC's certificate had been installed by hand.

## [0.16.2] - 2026-10-31

### Fixed
- `install.ps1` listed Hyper-V and WSL addresses and used the first one for the phone link. It now shows only
  network adapters with a default route.

## [0.16.1] - 2026-10-31

### Fixed
- Native frame rate under NVIDIA frame generation: the Reflex trace provider was enabled at a level that never
  delivered its markers.

## [0.16.0] - 2026-10-31

### Added
- Percent used for any "X Used" / "X Total" pair, worked out by the server (e.g. "GPU Memory Used" in % on every GPU
  vendor), and Total, Free and % for Windows' "D3D Dedicated Memory Used", the VRAM figure Task Manager shows.
- NVIDIA's power as a share of the card's power limit ("GPU Power" in %) is listed under Power, next to the watts,
  and the suggested GPU card falls back to it when a card reports no watts.
- Sensors that LibreHardwareMonitor only adds after its first readings (NVIDIA's "GPU Package" watts among them)
  show up instead of being missed until a restart.

## [0.15.0] - 2026-10-31

### Added
- The widget editor's sensor list shows every sensor's live reading, and percentages of how full memory or a drive
  is are listed with the sizes. Adding another reading to a card uses the same kind of list (device tabs, search,
  live readings, this card's device first), instead of a dropdown without values.

## [0.14.0] - 2026-10-31

### Added
- Separate desktop and phone layouts per PC, each with its own cards, sensors and sections. Phones and the phone
  preview use the phone layout, which follows the desktop one until it is changed. Graphs keep their history when
  cards are rebuilt or the layout switches.

## [0.13.0] - 2026-10-31

### Added
- Native frame rate next to the shown one under frame generation: NVIDIA DLSS FG (via Reflex PC Latency Stats),
  Intel XeSS FG and AMD Fluid Motion Frames, from public trace events (no injection). The suggested FPS card shows
  both. `tools\trace-frames.ps1` and `tools\analyze-frame-trace.py` record and summarise a game's frame events when
  the numbers look wrong.

## [0.12.0] - 2026-10-31

### Added
- Big-number cards (such as FPS): the number left, centered or right, in one of five sizes, with its unit right or
  left of it, centered above or below it, or hidden.

## [0.11.0] - 2026-10-31

### Added
- The dashboard keeps the screen on while it's open and in front (Screen Wake Lock): on phones and tablets by default,
  or always, or never (Settings). Browsers allow it on the secure (https) address.

## [0.10.0] - 2026-10-31

### Added
- Each card's title and subtitle (the device name by default) can be renamed, and the subtitle hidden.

## [0.9.0] - 2026-10-31

### Added
- Up to 3 sensors on one card: the main display plus up to 2 more readings, each shown as a value, a small graph, or
  both (e.g. CPU load with its power draw, a fan's % with its RPM), each placed left, center or right and in one of
  five text sizes, with the unit beside, above or below the number or hidden, with an editable label (by default the
  sensor's name when it shares the main reading's unit), styled like the main reading. The suggested layout puts
  power under the CPU and GPU load gauges and the VRAM % next to the VRAM bar. A graph card can hide its number
  (graph only).

## [0.8.0] - 2026-10-31

### Added
- Thickness of gauge arcs, bars and graph lines grows and shrinks with the card, and each card has a thickness setting
  (25 to 300 % of that) to make it thinner or thicker.

## [0.7.0] - 2026-10-31

### Added
- More ways to show a reading: five gauge styles (arc, semicircle, ring, needle, segments), horizontal and vertical
  bars, and a choice of where the value sits (gauge: top, center, bottom; bars: above, below, beside or inside).

## [0.6.0] - 2026-10-31

### Added
- Each PC stores its own dashboard (`/api/dashboard`), so every browser and phone sees the layout prepared for it;
  changes reach open dashboards right away.
- History: every sensor is recorded for 30 days (`/api/history`); tapping a card opens a chart with a 1, 3, 7, 14 or
  30 day slider, the average line, the lowest-to-highest band and the range's lowest, average and highest readings.

## [0.5.0] - 2026-10-31

### Added
- Dashboard layout: cards on a grid of columns and rows (4 columns on a phone), resizable by dragging a corner, with
  content that scales to the card; drag and drop reordering in edit mode; sections (titled groups that can be moved,
  renamed and folded away); a device icon on every card; phone preview on wide screens.

## [0.4.0] - 2026-10-31

### Added
- Widget editor: device tabs with reading filters nested under the chosen tab, instead of one long sensor list;
  a unit per reading (sizes in B, KB, MB, GB or TB whatever unit the sensor reports; data rates automatic or fixed
  in KB/s, MB/s, GB/s or Mbit/s, for extra readings too); bars for VRAM, RAM and drive space scale to the live total.

## [0.3.0] - 2026-10-31

### Added
- Frame rate, frame time and worst frame time for any GPU, from Windows' graphics present events (no injection);
  in the suggested layout.
- The dashboard keeps streaming while its tab is in the background, and keeps readings and graph history across
  reconnects.

## [0.2.0] - 2026-10-31

### Added
- Installable phone app: the dashboard is a PWA (manifest, icons, offline shell). Optional HTTPS on port 47812 with
  each PC's own name-constrained certificate authority and an auto-renewed server certificate, a phone setup page
  (`/trust`, iOS profile or certificate download with per-platform steps), and a QR code for it in the tray app
  (**Set up phone…**). A dashboard opened over HTTPS talks HTTPS to every PC.

## [0.1.0] - 2026-10-31

### Added
- Server (`syscom.exe`): LibreHardwareMonitor sensors plus drive space, shared polling loop (500 ms minimum,
  idle with no clients), WebSocket stream, password sign-in with signed tokens and lockout, LAN discovery,
  Windows service hosting.
- Web dashboard: multiple PCs with swipe navigation, gauges, bars, graphs and values, min/med/max colour
  thresholds, suggested layout, per-device polling interval, light/dark/auto theme, layout export/import.
- Component management: health and update checks for SYSCOM, web dashboard, LibreHardwareMonitor, PawnIO and
  the .NET runtime. One-click updates with checksum verification, backup and automatic rollback.
- Tray app (`syscom-tray.exe`): health icon, update notifications, component table, password change, restart.
- Packaging: `build\package.ps1` (full package with bundled runtime and PawnIO, update assets, feed.json),
  `install.ps1` / `uninstall.ps1`, CI and release workflows.

[0.17.0]: https://github.com/rotate4all/syscom/releases/tag/v0.17.0
