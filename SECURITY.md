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
- **HTTP and optional HTTPS.** The dashboard answers on HTTP (47810) and HTTPS (47812). Over HTTP, passwords and
  readings cross the LAN unencrypted; the HTTPS address encrypts them once a device trusts the PC's certificate.
  Each PC creates its own certificate authority (ECDSA P-256). Its private key never leaves
  `%ProgramData%\SYSCOM\tls` (SYSTEM and Administrators only), and it carries **critical name constraints** that
  limit it to private IPv4 ranges, `localhost`, `*.local` and the PC's name. A device that trusts it therefore can't
  be fooled about any public website, even if the key leaked. Set `HttpsEnabled` to `false` in `config.json` to turn
  HTTPS off. Don't expose either port to the internet; the installer's firewall rules only allow private networks.
- **Trusting the certificate.** Remove it from a phone at any time: iOS Settings → General → VPN & Device
  Management; Android Settings → Security → Encryption & credentials → User credentials. Deleting
  `%ProgramData%\SYSCOM\tls` makes the PC create a new authority, which every device must trust again.
- **Management API.** `/api/admin/*` (updates, password, restart) only accepts loopback requests carrying the key
  in `%ProgramData%\SYSCOM\admin.key`. That folder is restricted to SYSTEM and Administrators.
- **Updates.** Every download is checked against its publisher's checksum (see [docs/UPDATES.md](docs/UPDATES.md)),
  and files are only replaced by the elevated service and updater.
- **Kernel driver.** Low-level sensors need the PawnIO driver. SYSCOM flags PawnIO versions older than
  `MinPawnIoVersion` as a security error and offers the update.
