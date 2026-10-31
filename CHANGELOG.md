# Changelog

All notable changes are listed here. Versions follow [semantic versioning](https://semver.org/).

## [Unreleased]

### Added
- Server (`syscom.exe`): LibreHardwareMonitor sensors plus drive space, shared polling loop (500 ms minimum,
  idle with no clients), WebSocket stream, password sign-in with signed tokens and lockout, LAN discovery,
  Windows service hosting.
- Web dashboard: multiple PCs with swipe navigation, gauges, bars, graphs and values, min/med/max colour
  thresholds, suggested layout, per-device polling interval, light/dark/auto theme, layout export/import.
- Component management: health and update checks for SYSCOM, web dashboard, LibreHardwareMonitor, PawnIO and
  the .NET runtime. One-click updates with checksum verification, backup and automatic rollback.
- Tray app (`syscom-tray.exe`): health icon, update notifications, component table, password change, restart.
- Installable phone app: the dashboard is a PWA (manifest, icons, offline shell). Optional HTTPS on port 47812 with
  each PC's own name-constrained certificate authority and an auto-renewed server certificate, a phone setup page
  (`/trust`, iOS profile or certificate download with per-platform steps), and a QR code for it in the tray app
  (**Set up phone…**). A dashboard opened over HTTPS talks HTTPS to every PC.
- Frame rate, frame time and worst frame time for any GPU, from Windows' graphics present events (no injection);
  in the suggested layout.
- Widget editor: device tabs with reading filters nested under the chosen tab, instead of one long sensor list;
  a unit per reading (sizes in B, KB, MB, GB or TB whatever unit the sensor reports; data rates automatic or fixed
  in KB/s, MB/s, GB/s or Mbit/s, for extra readings too); bars for VRAM, RAM and drive space scale to the live total.
- Dashboard layout: cards on a grid of columns and rows (4 columns on a phone), resizable by dragging a corner, with
  content that scales to the card; drag and drop reordering in edit mode; sections (titled groups that can be moved,
  renamed and folded away); a device icon on every card; phone preview on wide screens.
- More ways to show a reading: five gauge styles (arc, semicircle, ring, needle, segments), horizontal and vertical
  bars, and a choice of where the value sits (gauge: top, center, bottom; bars: above, below, beside or inside).
- Thickness of gauge arcs, bars and graph lines grows and shrinks with the card, and each card has a thickness setting
  (25 to 300 % of that) to make it thinner or thicker.
- Up to 3 sensors on one card: the main display plus up to 2 more readings, each shown as a value, a small graph, or
  both (e.g. CPU load with its power draw, a fan's % with its RPM), each placed left, center or right and in one of
  five text sizes, with the unit beside, above or below the number or hidden, with an editable label (by default the sensor's name when it shares the main reading's unit),
  styled like the main reading. The suggested layout puts power under the CPU
  and GPU load gauges and the VRAM % next to the VRAM bar. A graph card can hide its number (graph only).
- Each card's title and subtitle (the device name by default) can be renamed, and the subtitle hidden.
- The dashboard keeps the screen on while it's open and in front (Screen Wake Lock): on phones and tablets by default,
  or always, or never (Settings). Browsers allow it on the secure (https) address.
- Big-number cards (such as FPS): the number left, centered or right, in one of five sizes, with its unit right or
  left of it, centered above or below it, or hidden.
- Native frame rate next to the shown one under frame generation: NVIDIA DLSS FG (via Reflex PC Latency Stats),
  Intel XeSS FG and AMD Fluid Motion Frames, from public trace events (no injection). The suggested FPS card shows
  both.
- Separate desktop and phone layouts per PC, each with its own cards, sensors and sections. Phones and the phone
  preview use the phone layout, which follows the desktop one until it is changed. Graphs keep their history when
  cards are rebuilt or the layout switches.
- The widget editor's sensor list shows every sensor's live reading, and percentages of how full memory or a drive
  is are listed with the sizes. NVIDIA's power as a share of the card's power limit ("GPU Power" in %) is listed
  under Power, next to the watts, and the suggested GPU card falls back to it when a card reports no watts.
  Sensors that LibreHardwareMonitor only adds after its first readings (NVIDIA's "GPU Package" watts among them)
  now show up instead of being missed until a restart. Adding another reading to a card uses the same kind of list (device tabs, search,
  live readings, this card's device first).
- Percent used for any "X Used" / "X Total" pair, worked out by the server (e.g. "GPU Memory Used" in % on every GPU
  vendor), and Total, Free and % for Windows' "D3D Dedicated Memory Used", the VRAM figure Task Manager shows.
- Each PC stores its own dashboard (`/api/dashboard`), so every browser and phone sees the layout prepared for it;
  changes reach open dashboards right away.
- History: every sensor is recorded for 30 days (`/api/history`); tapping a card opens a chart with a 1, 3, 7, 14 or
  30 day slider, the average line, the lowest-to-highest band and the range's lowest, average and highest readings.
- The dashboard keeps streaming while its tab is in the background, and keeps readings and graph history across
  reconnects.
- Packaging: `build\package.ps1` (full package with bundled runtime and PawnIO, update assets, feed.json),
  `install.ps1` / `uninstall.ps1`, CI and release workflows.
