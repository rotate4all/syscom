# API and protocol

All JSON uses camelCase. CORS allows any origin, so a dashboard served by one PC can talk to the others. There are
no cookies; auth is a bearer token.

## Public

| Method | Path | Auth | Response |
| --- | --- | --- | --- |
| GET | `/api/info` | none | `{ id, name, version, os, minIntervalMs, port, httpsPort, caFingerprint, caName, sources[] }`. `httpsPort` and the CA fields are `null` when HTTPS is off. |
| GET | `/api/health` | none | `{ status: "ok", version, sensors }`, used by the updater and installer |
| POST | `/api/login` | none | body `{ password }` → `{ token, expiresInDays }`. `401` wrong password. `429` + `Retry-After` when locked out (5 failures → 15 s, doubling, max 5 min). |
| POST | `/api/token` | bearer | a fresh token for a signed-in browser: `{ token, expiresInDays }`. The dashboard renews once a day, so a sign-in lasts `TokenLifetimeDays` (30) after its last use. |
| GET | `/api/sensors` | bearer | `[ { id, name, hardware, hardwareType, type, unit } ]` |
| GET | `/api/history?sensor=…&days=1…30` | bearer | `{ sensor, enabled, step (s), start (ms), avg[], min[], max[] }`, evenly spaced points; `null` = no data |
| GET | `/api/dashboard` | bearer | this PC's dashboard: `{ rev, widgets (desktop layout), phone (null = same as desktop), look, saved, current }`; `rev` 0 = nothing saved yet |
| PUT | `/api/dashboard` | bearer | body `{ widgets, phone, look, saved, current }` (max 2 MB) → `{ rev }`. Open dashboards get a `layout` message. |
| GET | `/api/dashboard/background` | bearer | the dashboard's background picture (`ETag` = its id, named in `look.image`); `404` when there is none |
| PUT | `/api/dashboard/background` | bearer | body: the picture (JPEG, PNG, WebP, GIF or AVIF, max 8 MB, `Content-Type` set) → `{ id }` |
| DELETE | `/api/dashboard/background` | bearer | removes it |
| GET | `/api/devices` | bearer | the device list kept on this PC for the dashboards it serves: `{ rev, devices: [ { id, name, url, httpsPort } ] }`. `url` is the PC's plain HTTP address (`null` for this PC itself). |
| PUT | `/api/devices` | bearer | body `{ devices: [...] }` (max 64 entries) → `{ rev }`. Open dashboards get a `devices` message. |
| GET | `/api/peers` | bearer | other SYSCOM servers heard on the LAN: `[ { id, name, url, httpsUrl } ]` (`httpsUrl` is `null` when that PC has HTTPS off) |
| GET | `/ws?token=…` | token in query | live stream, see below |
| GET | `/tls/syscom-ca.crt` | none | this PC's CA certificate (DER), for Android, Windows and Mac |
| GET | `/tls/syscom.mobileconfig` | none | the same CA as an iOS configuration profile |
| GET | `/trust` | none | phone setup page (trust the CA, open the HTTPS dashboard, install the app) |
| * | `/relay/{pc}/{path}` | passed through | relay to another SYSCOM PC on the LAN, see below |
| GET | `/*` | none | web dashboard |

Everything is served on both ports: HTTP (`Port`, 47810) and HTTPS (`HttpsPort`, 47812).

### Relay (`/relay/{pc}/…`)

A dashboard opened over HTTPS may only talk HTTPS, and every PC has its own certificate authority, so a phone would
have to trust every PC's certificate. Instead, the dashboard first tries a PC directly and, when that fails, goes
through the PC that served it: `/relay/{pc}/api/info`, `/relay/{pc}/ws?token=…` and so on are forwarded to that PC's
plain HTTP port on the LAN, WebSockets included.

- `{pc}` is a server id this PC knows (heard on the LAN, or in its device list), or `a.b.c.d-port` for a
  private-network IPv4 address (10/8, 172.16/12, 192.168/16, 169.254/16) that answers `/api/info` as a SYSCOM server.
- Only these paths are forwarded: `ws`, `api/info`, `api/health`, `api/login`, `api/token`, `api/sensors`,
  `api/history`, `api/peers`, `api/dashboard`, `api/dashboard/background`, `api/devices`. Never `/api/admin`.
- `Authorization` passes through unchanged; the target PC checks it. Unknown target or path: `404`; target not
  answering: `502`.

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

```jsonc
// server → client, on connect and after every save from any browser: fetch /api/dashboard when rev is new
{ "type": "layout", "rev": 12 }

// server → client, on connect and whenever the device list changes: fetch /api/devices when rev is new
{ "type": "devices", "rev": 3 }
```

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
| POST | `/api/admin/components/update` | `{ ids: ["lhm", …] }` (empty = all available) | `{ message, restarting }`. `restarting`: the updater took over and SYSCOM restarts; the outcome then shows as `lastUpdate` in the status. Refused (`restarting: true`) while a restart is pending. |
| GET | `/api/admin/settings` | | `{ autoUpdate, updateCheckHours }` |
| POST | `/api/admin/settings` | `{ autoUpdate: "syscom" \| "all" \| "off", updateCheckHours }` | the saved settings (see [UPDATES.md](UPDATES.md#automatic-updates)) |
| POST | `/api/admin/password` | `{ password }` | `{ message }`, signs out every browser |
| POST | `/api/admin/restart` | | `{ message, restarting }`, restarts via the updater (with health check) |

`ComponentStatus` and the other contracts are defined in [`src/Shared/Contracts.cs`](../src/Shared/Contracts.cs).
Component ids: `syscom`, `web`, `lhm`, `pawnio`, `dotnet`.
