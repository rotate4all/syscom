# Security

## Reporting a vulnerability

Please report security issues privately through GitHub's
[security advisories](https://github.com/rotate4all/syscom/security/advisories/new) rather than in a public issue.
You'll get a reply within a week.

## Threat model

SYSCOM is meant for a **trusted home or office LAN**.

- **Read-only monitoring.** Remote users can read sensor values and nothing else. There's no fan control, no
  command execution, no file access.
- **Sign-in.** Each server has its own password (PBKDF2-SHA256, 210k iterations). Successful logins get an
  HMAC-SHA256 signed token valid for 30 days. Five failed attempts from one address lock it out (15 s, doubling,
  up to 5 min). Changing the password invalidates every token.
- **Plain HTTP.** The dashboard is served over HTTP, so passwords and readings cross the LAN unencrypted. Don't
  expose port 47810 to the internet. The installer's firewall rules only allow private networks. Optional HTTPS is
  on the roadmap.
- **Management API.** `/api/admin/*` (updates, password, restart) only accepts loopback requests carrying the key
  in `%ProgramData%\SYSCOM\admin.key`. That folder is restricted to SYSTEM and Administrators.
- **Updates.** Every download is checked against its publisher's checksum (see [docs/UPDATES.md](docs/UPDATES.md)),
  and files are only replaced by the elevated service and updater.
- **Kernel driver.** Low-level sensors need the PawnIO driver. SYSCOM flags PawnIO versions older than
  `MinPawnIoVersion` as a security error and offers the update.
