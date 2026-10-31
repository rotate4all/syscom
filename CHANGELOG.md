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
  GB/MB choice for memory and storage sizes; bars for VRAM, RAM and drive space scale to the live total.
- The dashboard keeps streaming while its tab is in the background, and keeps readings and graph history across
  reconnects.
- Packaging: `build\package.ps1` (full package with bundled runtime and PawnIO, update assets, feed.json),
  `install.ps1` / `uninstall.ps1`, CI and release workflows.
