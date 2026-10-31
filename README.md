# SYSCOM · System Companion

[![CI](https://github.com/rotate4all/syscom/actions/workflows/ci.yml/badge.svg)](https://github.com/rotate4all/syscom/actions/workflows/ci.yml)
[![License: GPL v3](https://img.shields.io/badge/license-GPLv3-blue.svg)](LICENSE)

SYSCOM turns any phone, tablet or PC browser into a live hardware monitor for the computers on your network.
Install it on each Windows PC you want to watch. It runs quietly in the background and streams temperatures,
loads, clocks, fan speeds, memory, disk and network activity, and FPS. Open it from any browser, sign in, and
swipe between your machines.

There's no app to install on the phone and no GPU-Z, Afterburner or RivaTuner running in the background. Sensors
come from a single open-source library, and everything SYSCOM needs ships in one package that updates itself
component by component.

<p align="center">
  <img src="docs/images/dashboard-mobile-dark.png" width="260" alt="Dashboard on a phone, dark mode">
  &nbsp;
  <img src="docs/images/widget-editor.png" width="260" alt="Widget editor with min/med/max thresholds">
  &nbsp;
  <img src="docs/images/settings-mobile.png" width="260" alt="Settings: devices, polling interval, theme">
</p>
<p align="center">
  <img src="docs/images/dashboard-desktop-light.png" width="820" alt="Dashboard on a desktop browser, light mode">
</p>

> The screenshots use SYSCOM's built-in mock sensors (`--mock`); on a real PC you see your own hardware names.

## Features

- **Server-only, any browser as the client.** Each PC runs SYSCOM, and any device on the network opens
  `http://<pc>:47810`. Nothing to install on phones.
- **Many PCs, one dashboard.** Add every SYSCOM machine you own and swipe between them. SYSCOM servers find each
  other on the LAN automatically.
- **A configurable dashboard per PC.** Gauges, bars, live graphs and big numbers. Add, remove, reorder and resize
  widgets, and start from a suggested layout.
- **Min / med / max thresholds** per widget, each with its own colour (for example green, amber, red, or reversed for
  FPS).
- **Polling you control:** 1 s by default, down to 500 ms, set per PC. Servers only read hardware while someone is
  watching.
- **Light, dark or automatic theme.** Installable as a home-screen web app.
- **Password sign-in** per PC, with signed tokens and lockout after repeated failures.
- **Monitoring only.** No fan control, no overclocking, nothing that writes to your hardware.
- **A tray app** that shows the health of every component, checks for updates and installs them with one click,
  with automatic rollback if an update misbehaves.

## What it monitors

| Area | Sensors |
| --- | --- |
| CPU | total and per-core load, clocks, package/core temperatures, power, voltages |
| GPU (NVIDIA, AMD, Intel) | core/memory load, clocks, temperatures, hot spot, fan speed, power, VRAM used/free |
| Memory | load, used/available |
| Storage | temperatures (SMART), activity, read/write rates, used space, free/total space per drive |
| Motherboard | fan speeds, voltages, temperatures via Super I/O |
| Network | upload/download speed, data transferred |
| Other | battery, PSU (where supported) |
| FPS | AMD GPUs today (driver-reported). All GPUs through Windows' own present events: [planned](#roadmap) |

Sensors come from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), the
one hardware dependency. It covers what GPU-Z and MSI Afterburner report, without running either. Exactly what
appears depends on your hardware.

## Install

1. Download `SYSCOM-<version>-win-x64.zip` from [Releases](https://github.com/rotate4all/syscom/releases) and extract it.
2. Right-click `install.ps1` and choose **Run with PowerShell**. It asks for administrator rights.
3. Open one of the printed addresses (for example `http://192.168.1.20:47810`) on your phone and sign in with the
   printed password. Change it from the SYSCOM tray icon.

The installer puts SYSCOM in `C:\Program Files\SYSCOM`, registers the **SYSCOM** Windows service, opens TCP 47810
and UDP 47811 on private networks only, installs or updates the PawnIO driver if needed, and starts the tray app at
logon. Remove it with `uninstall.ps1` in the same folder.

**Requirements:** Windows 10/11 x64. Nothing else: the .NET runtime is bundled in the package.

### Why administrator rights and a driver?

CPU temperatures and motherboard sensors can only be read from kernel mode. LibreHardwareMonitor does that through
[PawnIO](https://pawnio.eu), a small signed driver. The installer installs the bundled PawnIO version, or updates
yours if it's older. The tray app warns you if PawnIO is ever missing or older than the minimum safe version
(older drivers had security issues). GPU, load, memory, disk and network sensors work without it.

## Using it

- **First visit:** the dashboard is empty. Tap **Suggested layout** to get CPU, GPU, RAM, disk and network
  widgets, or **Add widget** to pick sensors yourself.
- **Edit:** tap the pencil to enter edit mode. Each widget then gets ◀ ▶ (reorder), ✎ (sensor, display type,
  size, scale, thresholds) and 🗑.
- **Thresholds:** each widget has *min*, *med* and *max* values with a colour each. The widget takes the colour of the
  highest threshold the reading has reached. Leave a value empty to turn it off.
- **More PCs:** Settings → *Add device*. PCs found on your network are listed, or you can type an address. Swipe
  left or right (or use the arrow keys) to switch.
- **Polling:** Settings → per device, *Every … ms* (minimum 500).
- **Backup:** Settings → *Export layout* saves your dashboards to a file you can import in another browser.

### Put it on your home screen

The dashboard is a web app with its own icon and a full-screen window.

- **iPhone / iPad (Safari):** Share → *Add to Home Screen*. It opens full screen like a native app.
- **Android (Chrome):** ⋮ → *Add to Home screen*. Over plain HTTP, Chrome creates a shortcut that opens in the
  browser. A full install (own window, app drawer entry) needs HTTPS, which browsers require for installable apps.

Layouts are stored in the browser you use. Open the dashboard from the same PC address each time (bookmark it or
add it to your home screen) and all your devices and layouts will be there.

## Updating

The **tray app** (notification area → SYSCOM icon → *Manage SYSCOM…*) lists every component with its installed
version, the latest version and its health:

| Component | Updated from |
| --- | --- |
| SYSCOM (server, updater, tray app) | this repository's GitHub releases |
| Web dashboard | this repository's GitHub releases (applied live, no restart) |
| LibreHardwareMonitor | nuget.org |
| PawnIO driver | PawnIO's official GitHub releases |
| .NET runtime | Microsoft's official release feed (patch updates) |

**Update selected** or **Update all** downloads each piece, verifies its publisher's checksum, stops the service,
swaps the files, restarts and checks health. If SYSCOM doesn't come back healthy, the previous files are restored
automatically. Details are in [docs/UPDATES.md](docs/UPDATES.md).

## Building from source

```powershell
winget install Microsoft.DotNet.SDK.10
git clone https://github.com/rotate4all/syscom
cd syscom
pwsh build\package.ps1          # → dist\SYSCOM-<ver>-win-x64.zip plus update assets
```

For development you can run the server with fake sensors on any OS (`dotnet run --project src/Syscom.Server -- --mock`)
and edit `web/` with no build step. See [docs/BUILDING.md](docs/BUILDING.md).

## Documentation

- [Architecture](docs/ARCHITECTURE.md): how the pieces fit together
- [API and protocol](docs/API.md): HTTP endpoints and the WebSocket stream
- [Updates and releases](docs/UPDATES.md): component updates, rollback, feed format, cutting a release
- [Building](docs/BUILDING.md): dev setup, packaging, project layout
- [Security](SECURITY.md): threat model and how to report a vulnerability

## Roadmap

- FPS for every GPU vendor from Windows' graphics kernel present events (the same source PresentMon and FrameView
  use). No injection into games, so it's anti-cheat safe.
- Optional HTTPS for the dashboard.
- Per-widget history length and min/max markers on graphs.

## Credits and license

SYSCOM is licensed under the [GNU GPL v3](LICENSE). It builds on LibreHardwareMonitor (MPL-2.0), PawnIO and
.NET (MIT). See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Inspired by PITIKAPP.
