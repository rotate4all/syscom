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
- Packaging: `build\package.ps1` (full package with bundled runtime and PawnIO, update assets, feed.json),
  `install.ps1` / `uninstall.ps1`, CI and release workflows.
