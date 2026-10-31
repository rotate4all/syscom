SYSCOM - System Companion
=========================

Install
  1. Right-click install.ps1 > "Run with PowerShell" (it asks for administrator rights),
     or from an elevated PowerShell:  powershell -ExecutionPolicy Bypass -File .\install.ps1
  2. Open the dashboard address it prints from any phone or PC on your network
     and sign in with the password it shows. Change the password from the tray icon.
  3. To install the dashboard as an app on a phone: tray icon > "Set up phone..." and scan
     the QR code. The phone trusts this PC's own certificate once, then installs the app.

What gets installed
  C:\Program Files\SYSCOM         the app, its private .NET runtime (runtime\) and web dashboard (web\)
  C:\ProgramData\SYSCOM           settings, password hash, HTTPS certificates, update history (administrators only)
  Windows service "SYSCOM"        starts at boot
  Tray app (at logon)             health, update checks and one-click updates
  PawnIO driver                   only if missing or older than the bundled version
  Firewall rules                  TCP 47810 (HTTP), TCP 47812 (HTTPS), UDP 47811, private networks only

Bundled component versions are listed in components.json.

Uninstall
  powershell -ExecutionPolicy Bypass -File "C:\Program Files\SYSCOM\uninstall.ps1"

More: https://github.com/rotate4all/syscom
