# SYSCOM API

Read every sensor, its history and the dashboard's cards from scripts and other apps (Home Assistant, Stream Deck,
a Raspberry Pi display, a Python script…), with an API key. The dashboard shows this same reference in its **API**
window, with your PC's own address, sensors and cards filled into the examples.

- [What it is](#what-it-is) · [Keys](#keys) · [Endpoints](#endpoints) · [Sensors](#sensors) · [Dashboard](#dashboard) ·
  [PCs](#pcs) · [Live readings (WebSocket)](#live-readings-websocket) · [Categories and kinds](#categories-and-kinds)
- [How the dashboard talks to the PC](#how-the-dashboard-talks-to-the-pc): sign-in, keys, relay, the WebSocket protocol
- [Management API (tray app)](#management-api-tray-app)

<!-- api-reference:start -->
<!-- Generated from web/js/api-reference.js by tools/api-docs.mjs. Edit that file, then run: node tools/api-docs.mjs -->

## What it is

Everything the dashboard shows can be read by scripts and other apps: every sensor with its live value, the history, and the dashboard itself, card by card. Every PC running SYSCOM answers on its own address, e.g. `http://192.168.1.20:47810` (and `https://…:47812` when HTTPS is on). Answers are JSON.

The easy way in: build what you want to see in the dashboard, then open a card's **API** window (✎ edit mode → the `</>` button on the card, a section or the dashboard). It shows the exact address and ready-to-run examples for that card, filled in with this PC's address and ids.

## Keys

Create a key in the dashboard: Settings → **API keys** (or any API window). A **read** key can read everything below; a **full** key can also change the dashboard and the device list. The key is shown once: copy it then. Revoke a key there to stop it working at once. Keys are kept per PC.

Send the key in a header: `X-API-Key: syscom_…` or `Authorization: Bearer syscom_…`. For a quick look in a browser, GET requests and WebSockets also take `?key=syscom_…` in the address (it then ends up in browser history and logs, so prefer the header in scripts).

A missing, wrong or revoked key answers `401`; a read key trying to change something answers `401` too. Creating and revoking keys needs the dashboard (password sign-in): keys can't make more keys.

## Freshness

Readings are at most about a second old. When no dashboard is open, the first call may take up to a second while the PC takes a fresh reading; the PC then keeps reading every second for a minute after your last call. For faster or continuous updates, use the WebSocket stream below.

## Endpoints

| Method | Path | Needs | What |
| --- | --- | --- | --- |
| GET | [`/api/sensors`](#list-sensors-with-their-values) | read key | List sensors, with their values |
| GET | [`/api/sensors/{id}`](#one-sensor-with-its-value) | read key | One sensor, with its value |
| GET | [`/api/values`](#just-the-values) | read key | Just the values |
| GET | [`/api/categories`](#what-this-pc-has-by-category) | read key | What this PC has, by category |
| GET | [`/api/history`](#a-sensors-history-up-to-30-days) | read key | A sensor's history (up to 30 days) |
| GET | [`/api/widgets/{id}`](#one-card-with-its-readings) | read key | One card, with its readings |
| GET | [`/api/sections/{id}`](#one-section-with-all-its-cards) | read key | One section, with all its cards |
| GET | [`/api/dashboard/readings`](#the-whole-dashboard-with-every-reading) | read key | The whole dashboard, with every reading |
| GET | [`/api/dashboard`](#the-dashboard-as-stored-the-layouts-not-the-readings) | read key | The dashboard as stored (the layouts, not the readings) |
| PUT | [`/api/dashboard`](#replace-the-dashboard) | full-access key | Replace the dashboard |
| GET | [`/api/dashboard/background`](#the-background-picture) | read key | The background picture |
| PUT | [`/api/dashboard/background`](#set-the-background-picture) | full-access key | Set the background picture |
| GET | [`/api/devices`](#the-pcs-this-dashboard-shows) | read key | The PCs this dashboard shows |
| PUT | [`/api/devices`](#change-the-pcs-this-dashboard-shows) | full-access key | Change the PCs this dashboard shows |
| GET | [`/api/peers`](#syscom-pcs-heard-on-the-network) | read key | SYSCOM PCs heard on the network |
| GET | [`/api/info`](#this-pcs-name-and-version-no-key-needed) | no key | This PC's name and version (no key needed) |
| GET | [`/ws`](#live-readings-websocket) | read key | live readings, pushed every interval |

## Sensors

Every sensor this PC has, with its current reading. Values are in the sensor's own unit (`unit`) and rounded to 2 decimals; `null` means no reading right now.

### List sensors, with their values

`GET /api/sensors` · read key

All of them, or only those that match every filter you give.

| Parameter | |
| --- | --- |
| `category` | part of the PC: `cpu`, `gpu`, `game`, `memory`, `storage`, `network`, `motherboard`, `other` |
| `kind` | kind of reading: `temperature`, `load`, `clock`, `power`, `size`, `fan`, `throughput`, `fps`, `other` |
| `hardware` | part of the hardware name, e.g. `RTX` or `Ryzen` |
| `q` | search in names, hardware and ids |
| `type` | LibreHardwareMonitor sensor type, e.g. `Temperature`, `Load`, `Fan` |
| `ids` | comma-separated sensor ids |

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/sensors?category=gpu&kind=temperature"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/sensors?category=gpu&kind=temperature" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
$data | Format-Table hardware, name, value, unit
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/sensors?category=gpu&kind=temperature", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
for s in data:
    print(s["hardware"], s["name"], s["value"], s["unit"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/sensors?category=gpu&kind=temperature", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
for (const s of data) console.log(s.hardware, s.name, s.value, s.unit);
```

</details>

In a browser: `http://192.168.1.20:47810/api/sensors?category=gpu&kind=temperature&key=YOUR_API_KEY`

Answer:

```json
[
  {
    "id": "/amdcpu/0/temperature/2",
    "name": "Core (Tctl/Tdie)",
    "hardware": "AMD Ryzen 7 7800X3D",
    "hardwareType": "Cpu",
    "type": "Temperature",
    "unit": "°C",
    "category": "cpu",
    "kind": "temperature",
    "value": 61.25
  }
]
```

### One sensor, with its value

`GET /api/sensors/{id}` · read key

The sensor id goes in the path as it is (its slashes included), or URL-encoded.

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/sensors/amdcpu/0/temperature/2"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/sensors/amdcpu/0/temperature/2" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
"{0}: {1} {2}" -f $data.name, $data.value, $data.unit
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/sensors/amdcpu/0/temperature/2", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
print(data["name"], data["value"], data["unit"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/sensors/amdcpu/0/temperature/2", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
console.log(data.name, data.value, data.unit);
```

</details>

In a browser: `http://192.168.1.20:47810/api/sensors/amdcpu/0/temperature/2?key=YOUR_API_KEY`

Answer:

```json
{
  "id": "/amdcpu/0/temperature/2",
  "name": "Core (Tctl/Tdie)",
  "hardware": "AMD Ryzen 7 7800X3D",
  "hardwareType": "Cpu",
  "type": "Temperature",
  "unit": "°C",
  "category": "cpu",
  "kind": "temperature",
  "value": 61.25
}
```

### Just the values

`GET /api/values` · read key

The most compact answer, for polling: `{ t, values: { id: value } }`, `t` = when they were read (Unix ms). Takes the same filters as `/api/sensors`; ids that don't exist answer `null`.

| Parameter | |
| --- | --- |
| `ids` | comma-separated sensor ids |
| `category, kind, hardware, q, type` | as for `/api/sensors` |

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/values?ids=/amdcpu/0/load/0,/gpu-nvidia/0/temperature/0"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/values?ids=/amdcpu/0/load/0,/gpu-nvidia/0/temperature/0" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
$data.values."/amdcpu/0/load/0"
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/values?ids=/amdcpu/0/load/0,/gpu-nvidia/0/temperature/0", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
print(data["values"]["/amdcpu/0/load/0"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/values?ids=/amdcpu/0/load/0,/gpu-nvidia/0/temperature/0", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
console.log(data.values["/amdcpu/0/load/0"]);
```

</details>

In a browser: `http://192.168.1.20:47810/api/values?ids=/amdcpu/0/load/0,/gpu-nvidia/0/temperature/0&key=YOUR_API_KEY`

Answer:

```json
{
  "t": 1793430459731,
  "values": {
    "/amdcpu/0/load/0": 34.2,
    "/gpu-nvidia/0/temperature/0": 61
  }
}
```

### What this PC has, by category

`GET /api/categories` · read key

Each category with how many sensors it has, its hardware, and the kinds of readings in it (with counts).

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/categories"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/categories" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
$data | Format-Table id, count, hardware
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/categories", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
for c in data:
    print(c["id"], c["count"], ", ".join(c["hardware"]))
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/categories", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
for (const c of data) console.log(c.id, c.count, c.hardware.join(", "));
```

</details>

In a browser: `http://192.168.1.20:47810/api/categories?key=YOUR_API_KEY`

Answer:

```json
[
  {
    "id": "gpu",
    "name": "GPU",
    "count": 41,
    "hardware": [
      "NVIDIA GeForce RTX 5070"
    ],
    "kinds": [
      {
        "id": "temperature",
        "name": "Temperature",
        "count": 3
      },
      {
        "id": "load",
        "name": "Load",
        "count": 9
      }
    ]
  }
]
```

### A sensor's history (up to 30 days)

`GET /api/history` · read key

Evenly spaced points with the average, lowest and highest reading in each step; `null` = no data then. `step` is in seconds, `start` in Unix ms.

| Parameter | |
| --- | --- |
| `sensor` | the sensor id (required) |
| `days` | 1 to 30 (default 1) |

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/history?sensor=/amdcpu/0/temperature/2&days=1"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/history?sensor=/amdcpu/0/temperature/2&days=1" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
"highest today: {0}" -f ($data.max | Where-Object { $_ -ne $null } | Measure-Object -Maximum).Maximum
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/history?sensor=/amdcpu/0/temperature/2&days=1", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
peak = max(v for v in data["max"] if v is not None)
print("highest today:", peak)
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/history?sensor=/amdcpu/0/temperature/2&days=1", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
console.log("highest today:", Math.max(...data.max.filter((v) => v != null)));
```

</details>

In a browser: `http://192.168.1.20:47810/api/history?sensor=/amdcpu/0/temperature/2&days=1&key=YOUR_API_KEY`

Answer:

```json
{
  "sensor": "/amdcpu/0/temperature/2",
  "enabled": true,
  "step": 60,
  "start": 1793344080000,
  "avg": [
    48.1,
    47.9
  ],
  "min": [
    45,
    44.8
  ],
  "max": [
    55.3,
    52.1
  ]
}
```

## Dashboard

The cards of this PC's dashboard with what they show right now, worked out the way the dashboard does: the unit the card shows (`unit`, e.g. GB when the sensor reports MB), the formatted `text`, the scale (`min`, `max`; `max` is the live total for a "used" reading, `null` = automatic), the threshold reached (`level`, with its colour; `null` = below them all), and its extra readings. `interval` is the card's own refresh rate in ms (`null` = the dashboard's). Card and section ids are shown in each card's API window in the dashboard.

### One card, with its readings

`GET /api/widgets/{id}` · read key

| Parameter | |
| --- | --- |
| `layout` | `desktop` (default), `phone`, or the id or name of a saved layout |

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/widgets/k3f9x2"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/widgets/k3f9x2" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
"{0}: {1}" -f $data.widget.label, $data.widget.text
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/widgets/k3f9x2", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
w = data["widget"]
print(w["label"], w["text"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/widgets/k3f9x2", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
const w = data.widget;
console.log(w.label, w.text);
```

</details>

In a browser: `http://192.168.1.20:47810/api/widgets/k3f9x2?key=YOUR_API_KEY`

Answer:

```json
{
  "t": 1793430459731,
  "widget": {
    "id": "k3f9x2",
    "section": null,
    "type": "gauge",
    "label": "CPU",
    "subtitle": "AMD Ryzen 7 7800X3D",
    "interval": null,
    "sensor": {
      "id": "/amdcpu/0/load/0",
      "name": "CPU Total",
      "hardware": "AMD Ryzen 7 7800X3D",
      "unit": "%",
      "available": true
    },
    "value": 37.5,
    "unit": "%",
    "text": "38 %",
    "min": 0,
    "max": 100,
    "level": {
      "step": "min",
      "at": 30,
      "color": "#22c55e"
    },
    "extra": [
      {
        "sensor": {
          "id": "/amdcpu/0/power/0",
          "name": "Package",
          "hardware": "AMD Ryzen 7 7800X3D",
          "unit": "W",
          "available": true
        },
        "label": "",
        "value": 62.4,
        "unit": "W",
        "text": "62.4 W"
      }
    ]
  }
}
```

### One section, with all its cards

`GET /api/sections/{id}` · read key

| Parameter | |
| --- | --- |
| `layout` | as above |

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/sections/q8d1vz"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/sections/q8d1vz" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
$data.section.widgets | Format-Table label, text
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/sections/q8d1vz", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
for w in data["section"]["widgets"]:
    print(w["label"], w["text"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/sections/q8d1vz", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
for (const w of data.section.widgets) console.log(w.label, w.text);
```

</details>

In a browser: `http://192.168.1.20:47810/api/sections/q8d1vz?key=YOUR_API_KEY`

Answer:

```json
{
  "t": 1793430459731,
  "section": {
    "id": "q8d1vz",
    "title": "Graphics",
    "widgets": [
      "…cards, as above…"
    ]
  }
}
```

### The whole dashboard, with every reading

`GET /api/dashboard/readings` · read key

`widgets` are the cards above the first section; then each section with its cards.

| Parameter | |
| --- | --- |
| `layout` | as above |

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/dashboard/readings"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/dashboard/readings" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
@($data.widgets) + @($data.sections.widgets) | Format-Table label, text
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/dashboard/readings", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
for w in data["widgets"] + [w for s in data["sections"] for w in s["widgets"]]:
    print(w["label"], w["text"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/dashboard/readings", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
for (const w of [...data.widgets, ...data.sections.flatMap((s) => s.widgets)]) console.log(w.label, w.text);
```

</details>

In a browser: `http://192.168.1.20:47810/api/dashboard/readings?key=YOUR_API_KEY`

Answer:

```json
{
  "pc": "GAMING-PC",
  "layout": "desktop",
  "t": 1793430459731,
  "widgets": [
    "…cards…"
  ],
  "sections": [
    {
      "id": "q8d1vz",
      "title": "Graphics",
      "widgets": [
        "…cards…"
      ]
    }
  ]
}
```

### The dashboard as stored (the layouts, not the readings)

`GET /api/dashboard` · read key

`{ rev, widgets (desktop layout), phone (null = same as desktop), look, saved, current }`; `rev` 0 = nothing saved yet. Each card is `{ id, sensor, type, w, h, label, min, max, thresholds, … }`, each section `{ id, kind: "section", title }`.

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/dashboard"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/dashboard" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
"{0} cards and sections on the desktop layout" -f $data.widgets.Count
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/dashboard", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
print(len(data["widgets"]), "cards and sections on the desktop layout")
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/dashboard", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
console.log(data.widgets.length, "cards and sections on the desktop layout");
```

</details>

In a browser: `http://192.168.1.20:47810/api/dashboard?key=YOUR_API_KEY`

Answer:

```json
{
  "rev": 12,
  "updated": 1793430459731,
  "widgets": [
    "…"
  ],
  "phone": null,
  "look": {
    "bg": "none",
    "alpha": 100,
    "round": 100,
    "anim": "smooth"
  },
  "saved": [],
  "current": {}
}
```

### Replace the dashboard

`PUT /api/dashboard` · full-access key

Body: `{ widgets, phone, look, saved, current }` as `GET /api/dashboard` returns them (max 2 MB). Answers `{ rev }`; open dashboards reload it at once. Read it first and send it back changed, so nothing else is lost.

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/dashboard" -o dashboard.json
# change dashboard.json, then send it back:
curl -X PUT -H "X-API-Key: YOUR_API_KEY" -H "Content-Type: application/json" --data-binary @dashboard.json "http://192.168.1.20:47810/api/dashboard"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$h = @{ "X-API-Key" = "YOUR_API_KEY" }
$d = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/dashboard" -Headers $h
$d.widgets[0].label = "CPU"   # change something
$data = Invoke-RestMethod -Method Put -Uri "http://192.168.1.20:47810/api/dashboard" -Headers $h -ContentType "application/json" -Body ($d | ConvertTo-Json -Depth 20)
"saved, revision {0}" -f $data.rev
```

```python
import json, urllib.request

h = {"X-API-Key": "YOUR_API_KEY"}
with urllib.request.urlopen(urllib.request.Request("http://192.168.1.20:47810/api/dashboard", headers=h)) as r:
    d = json.load(r)
d["widgets"][0]["label"] = "CPU"  # change something
req = urllib.request.Request("http://192.168.1.20:47810/api/dashboard", method="PUT", data=json.dumps(d).encode(),
                             headers={**h, "Content-Type": "application/json"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
print("saved, revision", data["rev"])
```

```javascript
const h = { "X-API-Key": "YOUR_API_KEY" };
const d = await (await fetch("http://192.168.1.20:47810/api/dashboard", { headers: h })).json();
d.widgets[0].label = "CPU";   // change something
const r = await fetch("http://192.168.1.20:47810/api/dashboard", { method: "PUT", headers: { ...h, "Content-Type": "application/json" }, body: JSON.stringify(d) });
const data = await r.json();
console.log("saved, revision", data.rev);
```

</details>

Answer:

```json
{
  "rev": 13
}
```

### The background picture

`GET /api/dashboard/background` · read key

The picture itself, with its type in `Content-Type`; `404` when there is none. It shows when the dashboard's `look.bg` is `"image"`; `look.image` is its id (also the `ETag`).

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/dashboard/background" -o background
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
Invoke-WebRequest -Uri "http://192.168.1.20:47810/api/dashboard/background" -Headers @{ "X-API-Key" = "YOUR_API_KEY" } -OutFile background
```

```python
import urllib.request

req = urllib.request.Request("http://192.168.1.20:47810/api/dashboard/background", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r, open("background", "wb") as f:
    f.write(r.read())
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/dashboard/background", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const picture = await r.blob();   // r.headers.get("Content-Type") says which kind
```

</details>

In a browser: `http://192.168.1.20:47810/api/dashboard/background?key=YOUR_API_KEY`

Answer: the picture (JPEG, PNG, WebP, GIF or AVIF).

### Set the background picture

`PUT /api/dashboard/background` · full-access key

Body: the picture (JPEG, PNG, WebP, GIF or AVIF, up to 8 MB) with its `Content-Type`. Answers `{ id }`. To show it, set `look.bg` to `"image"` and `look.image` to that id in the dashboard (`PUT /api/dashboard`; `look.fit` is `cover`, `contain` or `tile`, `look.dim` 0–80). `DELETE` on the same address removes the picture.

```bash
curl -X PUT -H "X-API-Key: YOUR_API_KEY" -H "Content-Type: image/jpeg" --data-binary @picture.jpg "http://192.168.1.20:47810/api/dashboard/background"
curl -X DELETE -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/dashboard/background"   # removes it
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Method Put -Uri "http://192.168.1.20:47810/api/dashboard/background" -Headers @{ "X-API-Key" = "YOUR_API_KEY" } -ContentType "image/jpeg" -InFile picture.jpg
"picture id: {0}" -f $data.id
```

```python
import json, urllib.request

with open("picture.jpg", "rb") as f:
    req = urllib.request.Request("http://192.168.1.20:47810/api/dashboard/background", method="PUT", data=f.read(),
                                 headers={"X-API-Key": "YOUR_API_KEY", "Content-Type": "image/jpeg"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
print("picture id:", data["id"])
```

```javascript
// picture: a File from <input type="file">, or any Blob
const r = await fetch("http://192.168.1.20:47810/api/dashboard/background", { method: "PUT", headers: { "X-API-Key": "YOUR_API_KEY", "Content-Type": picture.type }, body: picture });
const data = await r.json();
console.log("picture id:", data.id);
```

</details>

Answer:

```json
{
  "id": "5d41402abc4b2a76"
}
```

## PCs

The other SYSCOM PCs, as the dashboard knows them.

### The PCs this dashboard shows

`GET /api/devices` · read key

`{ rev, devices: [ { id, name, url, httpsPort } ] }`; `url` is the PC's plain HTTP address (`null` for this PC itself); the dashboard swipes through them in this order.

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/devices"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/devices" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
$data.devices | Format-Table name, url
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/devices", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
for d in data["devices"]:
    print(d["name"], d["url"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/devices", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
for (const d of data.devices) console.log(d.name, d.url);
```

</details>

In a browser: `http://192.168.1.20:47810/api/devices?key=YOUR_API_KEY`

Answer:

```json
{
  "rev": 3,
  "devices": [
    {
      "id": "a1b2",
      "name": "GAMING-PC",
      "url": null,
      "httpsPort": 47812
    },
    {
      "id": "c3d4",
      "name": "HP-NAS",
      "url": "http://192.168.1.30:47810",
      "httpsPort": 47812
    }
  ]
}
```

### Change the PCs this dashboard shows

`PUT /api/devices` · full-access key

Body: `{ devices: [ { id, name, url, httpsPort } ] }` (up to 64), the whole list in the order to show. Answers `{ rev }`; open dashboards pick it up at once. Each needs an `id` or a `url`; a PC's `id` and `httpsPort` are in its `/api/info` (or `GET /api/peers`).

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/devices" -o devices.json
# change devices.json, then send it back:
curl -X PUT -H "X-API-Key: YOUR_API_KEY" -H "Content-Type: application/json" --data-binary @devices.json "http://192.168.1.20:47810/api/devices"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$h = @{ "X-API-Key" = "YOUR_API_KEY" }
$d = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/devices" -Headers $h
$d.devices = @($d.devices | Where-Object name -ne "HP-NAS")   # e.g. remove a PC
$data = Invoke-RestMethod -Method Put -Uri "http://192.168.1.20:47810/api/devices" -Headers $h -ContentType "application/json" -Body (@{ devices = $d.devices } | ConvertTo-Json -Depth 5)
"saved, revision {0}" -f $data.rev
```

```python
import json, urllib.request

h = {"X-API-Key": "YOUR_API_KEY"}
with urllib.request.urlopen(urllib.request.Request("http://192.168.1.20:47810/api/devices", headers=h)) as r:
    devices = json.load(r)["devices"]
devices = [d for d in devices if d["name"] != "HP-NAS"]  # e.g. remove a PC
req = urllib.request.Request("http://192.168.1.20:47810/api/devices", method="PUT", data=json.dumps({"devices": devices}).encode(),
                             headers={**h, "Content-Type": "application/json"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
print("saved, revision", data["rev"])
```

```javascript
const h = { "X-API-Key": "YOUR_API_KEY" };
const { devices } = await (await fetch("http://192.168.1.20:47810/api/devices", { headers: h })).json();
const keep = devices.filter((d) => d.name !== "HP-NAS");   // e.g. remove a PC
const r = await fetch("http://192.168.1.20:47810/api/devices", { method: "PUT", headers: { ...h, "Content-Type": "application/json" }, body: JSON.stringify({ devices: keep }) });
const data = await r.json();
console.log("saved, revision", data.rev);
```

</details>

Answer:

```json
{
  "rev": 4
}
```

### SYSCOM PCs heard on the network

`GET /api/peers` · read key

```bash
curl -H "X-API-Key: YOUR_API_KEY" "http://192.168.1.20:47810/api/peers"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/peers" -Headers @{ "X-API-Key" = "YOUR_API_KEY" }
$data | Format-Table name, url
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/peers", headers={"X-API-Key": "YOUR_API_KEY"})
with urllib.request.urlopen(req) as r:
    data = json.load(r)
for p in data:
    print(p["name"], p["url"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/peers", { headers: { "X-API-Key": "YOUR_API_KEY" } });
const data = await r.json();
for (const p of data) console.log(p.name, p.url);
```

</details>

In a browser: `http://192.168.1.20:47810/api/peers?key=YOUR_API_KEY`

Answer:

```json
[
  {
    "id": "c3d4",
    "name": "HP-NAS",
    "url": "http://192.168.1.30:47810",
    "httpsUrl": "https://192.168.1.30:47812"
  }
]
```

### This PC's name and version (no key needed)

`GET /api/info` · no key

```bash
curl "http://192.168.1.20:47810/api/info"
```

<details><summary>PowerShell, Python, JavaScript</summary>

```powershell
$data = Invoke-RestMethod -Uri "http://192.168.1.20:47810/api/info"
"{0} runs SYSCOM {1}" -f $data.name, $data.version
```

```python
import json, urllib.request
req = urllib.request.Request("http://192.168.1.20:47810/api/info")
with urllib.request.urlopen(req) as r:
    data = json.load(r)
print(data["name"], data["version"])
```

```javascript
const r = await fetch("http://192.168.1.20:47810/api/info");
const data = await r.json();
console.log(data.name, data.version);
```

</details>

Answer:

```json
{
  "id": "9f1c…",
  "name": "GAMING-PC",
  "version": "0.18.0",
  "os": "Microsoft Windows NT 10.0.26100.0",
  "minIntervalMs": 500,
  "port": 47810,
  "httpsPort": 47812,
  "sources": [
    "LibreHardwareMonitor",
    "Drive space",
    "Frame rate"
  ]
}
```

## Live readings (WebSocket)

For live readings, open a WebSocket to `/ws?key=…` (the key in the address: browsers can't send headers on WebSockets) and say which sensors you want and how often (ms, 500 to 60000). The PC first sends its sensor list (`catalog`), confirms your subscription (`subscribed`, with the order of the values), then sends a `data` frame every interval: `{ "type": "data", "t": …, "v": [ … ] }`, values in that order.

Several rates on one connection: `{ "type": "subscribe", "streams": [ { "id": 1, "interval": 500, "sensors": [ … ] }, { "id": 2, "interval": 5000, "sensors": [ … ] } ] }`; each `data` frame then names its stream in `"s"`. Omit `sensors` for every sensor. A bad key closes the socket with code 4401.

```javascript
const ws = new WebSocket("ws://192.168.1.20:47810/ws?key=YOUR_API_KEY");
let order = [];
ws.onopen = () => ws.send(JSON.stringify({
  type: "subscribe", interval: 1000, sensors: ["/amdcpu/0/load/0","/gpu-nvidia/0/temperature/0"],
}));
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.type === "subscribed") order = m.sensors;
  if (m.type === "data") console.log(Object.fromEntries(order.map((id, i) => [id, m.v[i]])));
};
```

```python
# pip install websockets
import asyncio, json, websockets

async def main():
    async with websockets.connect("ws://192.168.1.20:47810/ws?key=YOUR_API_KEY") as ws:
        await ws.send(json.dumps({"type": "subscribe", "interval": 1000, "sensors": ["/amdcpu/0/load/0","/gpu-nvidia/0/temperature/0"]}))
        order = []
        async for raw in ws:
            m = json.loads(raw)
            if m["type"] == "subscribed":
                order = m["sensors"]
            elif m["type"] == "data":
                print(dict(zip(order, m["v"])))

asyncio.run(main())
```

## Categories and kinds

Every sensor has one of each (`category`, `kind` in its answer); filter with `?category=` and `?kind=`.

| category | | |
| --- | --- | --- |
| `cpu` | CPU | processor load, clocks, temperatures, power |
| `gpu` | GPU | graphics load, clocks, temperatures, power, VRAM, fans |
| `game` | Game | frame rate, native frame rate, frame time of the game in front |
| `memory` | Memory | RAM used, available, load |
| `storage` | Drives | drive temperatures, activity, used and free space |
| `network` | Network | download and upload speed, data sent |
| `motherboard` | Motherboard | board sensors, fans, pumps, power supply, battery |
| `other` | Other | anything else |

| kind | | units |
| --- | --- | --- |
| `temperature` | Temperature | °C |
| `load` | Load | % |
| `clock` | Clock | MHz |
| `power` | Power | W, V, A (and NVIDIA power in % of its limit) |
| `size` | Memory & space | GB, MB (and how full in %) |
| `fan` | Fans | RPM, % |
| `throughput` | Data rate | B/s |
| `fps` | Frames | FPS, ms |
| `other` | Other |  |

Example: everything about the GPU's temperatures: `http://192.168.1.20:47810/api/sensors?category=gpu&kind=temperature`

<!-- api-reference:end -->

## How the dashboard talks to the PC

All JSON uses camelCase. CORS allows any origin, so a dashboard served by one PC can talk to the others. There are
no cookies: the dashboard signs in with the password and sends the token it gets as `Authorization: Bearer …`;
scripts send an API key instead (see [Keys](#keys)). Wherever a token works, so does a key, with these exceptions:
a read key can't change anything, and only a signed-in dashboard can renew its token or manage keys.

| Method | Path | Auth | Response |
| --- | --- | --- | --- |
| GET | `/api/info` | none | `{ id, name, version, os, minIntervalMs, port, httpsPort, caFingerprint, caName, sources[] }`. `httpsPort` and the CA fields are `null` when HTTPS is off. |
| GET | `/api/health` | none | `{ status: "ok", version, sensors }`, used by the updater and installer |
| POST | `/api/login` | none | body `{ password }` → `{ token, expiresInDays }`. `401` wrong password. `429` + `Retry-After` when locked out (5 failures → 15 s, doubling, max 5 min). |
| POST | `/api/token` | dashboard | a fresh token for a signed-in browser: `{ token, expiresInDays }`. The dashboard renews once a day, so a sign-in lasts `TokenLifetimeDays` (30) after its last use. |
| GET | `/api/keys` | dashboard | the API keys: `[ { id, name, access, prefix, created, lastUsed } ]` (the keys themselves are never stored, only a hash) |
| POST | `/api/keys` | dashboard | body `{ name, access: "read" \| "full" }` → `{ key: { id, … }, secret }`; `secret` is the key, shown this once. At most 50 keys. |
| DELETE | `/api/keys/{id}` | dashboard | revokes the key at once |
| GET | `/api/sensors` … | bearer or key | see [Sensors](#sensors) and [Dashboard](#dashboard) above |
| GET | `/api/history?sensor=…&days=1…30` | bearer | `{ sensor, enabled, step (s), start (ms), avg[], min[], max[] }`, evenly spaced points; `null` = no data |
| GET | `/api/dashboard` | bearer | this PC's dashboard: `{ rev, widgets (desktop layout), phone (null = same as desktop), look, saved, current }`; `rev` 0 = nothing saved yet |
| PUT | `/api/dashboard` | bearer | body `{ widgets, phone, look, saved, current }` (max 2 MB) → `{ rev }`. Open dashboards get a `layout` message. |
| GET | `/api/dashboard/background` | bearer | the dashboard's background picture (`ETag` = its id, named in `look.image`); `404` when there is none |
| PUT | `/api/dashboard/background` | bearer | body: the picture (JPEG, PNG, WebP, GIF or AVIF, max 8 MB, `Content-Type` set) → `{ id }` |
| DELETE | `/api/dashboard/background` | bearer | removes it |
| GET | `/api/devices` | bearer | the device list kept on this PC for the dashboards it serves: `{ rev, devices: [ { id, name, url, httpsPort } ] }`. `url` is the PC's plain HTTP address (`null` for this PC itself). |
| PUT | `/api/devices` | bearer | body `{ devices: [...] }` (max 64 entries) → `{ rev }`. Open dashboards get a `devices` message. |
| GET | `/api/peers` | bearer | other SYSCOM servers heard on the LAN: `[ { id, name, url, httpsUrl } ]` (`httpsUrl` is `null` when that PC has HTTPS off) |
| GET | `/ws?token=…` or `/ws?key=…` | token or key in query | live stream, see below |
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
  `api/sensors/…`, `api/values`, `api/categories`, `api/history`, `api/peers`, `api/dashboard`,
  `api/dashboard/background`, `api/dashboard/readings`, `api/widgets/…`, `api/sections/…`, `api/devices`,
  `api/keys`, `api/keys/…`. Never `/api/admin`.
- `Authorization` passes through unchanged; the target PC checks it. Unknown target or path: `404`; target not
  answering: `502`.

## WebSocket stream (`/ws`)

Browsers can't set headers on WebSockets, so the token (or `key=`) travels in the query string. A bad or expired
token or key is accepted and then immediately closed with code **4401**. The page shows the sign-in form instead of "offline".

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
// client → server: several rates on one connection (the dashboard does this when cards have their own refresh rate)
{ "type": "subscribe", "interval": 1000, "streams": [
  { "id": 1000, "interval": 1000, "sensors": [ "/amdcpu/0/load/0" ] },
  { "id": 500, "interval": 500, "sensors": [ "/frames/fps" ] } ] }

// server → client
{ "type": "subscribed", "streams": [ { "id": 1000, "interval": 1000, "sensors": [ … ] }, { "id": 500, … } ] }
{ "type": "data", "s": 500, "t": 1793430459731, "v": [ 143 ] }      // "s" = which stream
```

The PC reads the hardware as often as the fastest stream needs. A stream without `sensors` gets every sensor. PCs
older than 0.18 ignore `streams` and use `interval` and `sensors` (the dashboard sends both, every sensor in
`sensors`), so their cards all refresh at the dashboard's rate.

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
