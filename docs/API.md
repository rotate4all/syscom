# API and protocol

All JSON uses camelCase. CORS allows any origin, so a dashboard served by one PC can talk to the others. There are
no cookies; auth is a bearer token.

## Public

| Method | Path | Auth | Response |
| --- | --- | --- | --- |
| GET | `/api/info` | none | `{ id, name, version, os, minIntervalMs, port, httpsPort, caFingerprint, caName, sources[] }`. `httpsPort` and the CA fields are `null` when HTTPS is off. |
| GET | `/api/health` | none | `{ status: "ok", version, sensors }`, used by the updater and installer |
| POST | `/api/login` | none | body `{ password }` → `{ token, expiresInDays }`. `401` wrong password. `429` + `Retry-After` when locked out (5 failures → 15 s, doubling, max 5 min). |
| GET | `/api/sensors` | bearer | `[ { id, name, hardware, hardwareType, type, unit } ]` |
| GET | `/api/peers` | bearer | other SYSCOM servers heard on the LAN: `[ { id, name, url, httpsUrl } ]` (`httpsUrl` is `null` when that PC has HTTPS off) |
| GET | `/ws?token=…` | token in query | live stream, see below |
| GET | `/tls/syscom-ca.crt` | none | this PC's CA certificate (DER), for Android, Windows and Mac |
| GET | `/tls/syscom.mobileconfig` | none | the same CA as an iOS configuration profile |
| GET | `/trust` | none | phone setup page (trust the CA, open the HTTPS dashboard, install the app) |
| GET | `/*` | none | web dashboard |

Everything is served on both ports: HTTP (`Port`, 47810) and HTTPS (`HttpsPort`, 47812).

## WebSocket stream (`/ws`)

Browsers can't set headers on WebSockets, so the token travels in the query string. A bad or expired token is
accepted and then immediately closed with code **4401**. The page shows the sign-in form instead of "offline".

Messages are JSON text frames:

```jsonc
// server → client, on connect and whenever hardware changes (USB drive added, …)
{ "type": "catalog", "version": 1, "sensors": [ { "id": "/amdcpu/0/load/0", "name": "CPU Total",
  "hardware": "AMD Ryzen 7 7800X3D", "hardwareType": "Cpu", "type": "Load", "unit": "%" } ] }

// client → server: what to send and how often (ms, clamped to minIntervalMs … 60000). Omit "sensors" for all.
{ "type": "subscribe", "interval": 1000, "sensors": [ "/amdcpu/0/load/0", "/gpu-nvidia/0/temperature/0" ] }

// server → client: confirms the order used by data frames
{ "type": "subscribed", "interval": 1000, "sensors": [ "/amdcpu/0/load/0", "/gpu-nvidia/0/temperature/0" ] }

// server → client, every interval: values in "subscribed" order; null = no reading
{ "type": "data", "t": 1793430459731, "v": [ 34.2, 61 ] }
```

No data is sent until the client subscribes. Values are rounded to 2 decimals.

Units by sensor type: Temperature `°C`, Load/Control/Level `%`, Clock `MHz`, Power `W`, Voltage `V`,
Current `A`, Fan `RPM`, Data `GB`, SmallData `MB`, Throughput `B/s` (the dashboard scales to KB/s, MB/s),
Fps `FPS`, Frametime `ms`.

Frame rate sensors (Windows): `/frames/fps`, `/frames/frametime` (average over the last second) and
`/frames/frametime-worst` (longest frame in that second). Their `hardware` is the name of the app being measured,
or `No game running`; the catalog is re-sent when it changes.

## Management API (tray app)

`/api/admin/*` only answers requests from **loopback** that carry header `X-Syscom-Admin: <contents of
%ProgramData%\SYSCOM\admin.key>`. Everything else gets `403`.

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| GET | `/api/admin/status` | | `ServerStatus`: name, version, port, urls, clients, sensors, runningAsService, updateFeedUrl, lastUpdate, httpsPort, caFingerprint |
| GET | `/api/admin/components` | | `ComponentStatus[]` |
| POST | `/api/admin/components/check` | | checks every source, returns `ComponentStatus[]` |
| POST | `/api/admin/components/update` | `{ ids: ["lhm", …] }` (empty = all available) | `{ message }` |
| POST | `/api/admin/password` | `{ password }` | `{ message }`, signs out every browser |
| POST | `/api/admin/restart` | | `{ message }`, restarts via the updater (with health check) |

`ComponentStatus` and the other contracts are defined in [`src/Shared/Contracts.cs`](../src/Shared/Contracts.cs).
Component ids: `syscom`, `web`, `lhm`, `pawnio`, `dotnet`.
