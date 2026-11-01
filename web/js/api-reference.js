// The SYSCOM API reference, in one place: the dashboard shows it (with this PC's own address, sensors and cards as
// the examples) and tools/api-docs.mjs writes docs/API.md from it, so both always say the same. Plain data and
// string builders only, no DOM.

export const KEY_HEADER = 'X-API-Key';
export const KEY_PLACEHOLDER = 'YOUR_API_KEY';

/** Sensor categories (?category=) and kinds (?kind=): [id, name, what is in it]. */
export const CATEGORIES = [
  ['cpu', 'CPU', 'processor load, clocks, temperatures, power'],
  ['gpu', 'GPU', 'graphics load, clocks, temperatures, power, VRAM, fans'],
  ['game', 'Game', 'frame rate, native frame rate, frame time of the game in front'],
  ['memory', 'Memory', 'RAM used, available, load'],
  ['storage', 'Drives', 'drive temperatures, activity, used and free space'],
  ['network', 'Network', 'download and upload speed, data sent'],
  ['motherboard', 'Motherboard', 'board sensors, fans, pumps, power supply, battery'],
  ['other', 'Other', 'anything else'],
];
export const KINDS = [
  ['temperature', 'Temperature', '°C'],
  ['load', 'Load', '%'],
  ['clock', 'Clock', 'MHz'],
  ['power', 'Power', 'W, V, A (and NVIDIA power in % of its limit)'],
  ['size', 'Memory & space', 'GB, MB (and how full in %)'],
  ['fan', 'Fans', 'RPM, %'],
  ['throughput', 'Data rate', 'B/s'],
  ['fps', 'Frames', 'FPS, ms'],
  ['other', 'Other', ''],
];

/** Example values for the docs on GitHub; the dashboard uses its own PC's. */
export const SAMPLE = {
  base: 'http://192.168.1.20:47810',
  key: KEY_PLACEHOLDER,
  pc: 'GAMING-PC',
  sensor: { id: '/amdcpu/0/temperature/2', name: 'Core (Tctl/Tdie)', hardware: 'AMD Ryzen 7 7800X3D', unit: '°C' },
  sensors: ['/amdcpu/0/load/0', '/gpu-nvidia/0/temperature/0'],
  category: 'gpu',
  kind: 'temperature',
  widget: { id: 'k3f9x2', label: 'CPU' },
  section: { id: 'q8d1vz', title: 'Graphics' },
};

const json = (x) => JSON.stringify(x, null, 2);

/**
 * Endpoints in groups. path: with {placeholders}; call(ctx) → the example request { path, query? };
 * pick: what to do with the answer in each language (the variable is "data").
 */
export const GROUPS = [
  {
    title: 'Sensors',
    about: 'Every sensor this PC has, with its current reading. Values are in the sensor\'s own unit (`unit`) and rounded to 2 decimals; `null` means no reading right now.',
    endpoints: [
      {
        id: 'sensors', method: 'GET', path: '/api/sensors', access: 'read',
        summary: 'List sensors, with their values',
        about: 'All of them, or only those that match every filter you give.',
        params: [
          ['category', `part of the PC: ${CATEGORIES.map(([id]) => `\`${id}\``).join(', ')}`],
          ['kind', `kind of reading: ${KINDS.map(([id]) => `\`${id}\``).join(', ')}`],
          ['hardware', 'part of the hardware name, e.g. `RTX` or `Ryzen`'],
          ['q', 'search in names, hardware and ids'],
          ['type', 'LibreHardwareMonitor sensor type, e.g. `Temperature`, `Load`, `Fan`'],
          ['ids', 'comma-separated sensor ids'],
        ],
        call: (c) => ({ path: '/api/sensors', query: { category: c.category, kind: c.kind } }),
        pick: {
          python: 'for s in data:\n    print(s["hardware"], s["name"], s["value"], s["unit"])',
          javascript: 'for (const s of data) console.log(s.hardware, s.name, s.value, s.unit);',
          powershell: '$data | Format-Table hardware, name, value, unit',
        },
        response: (c) => json([{ id: c.sensor.id, name: c.sensor.name, hardware: c.sensor.hardware, hardwareType: 'Cpu', type: 'Temperature', unit: c.sensor.unit, category: 'cpu', kind: 'temperature', value: 61.25 }]),
      },
      {
        id: 'sensor', method: 'GET', path: '/api/sensors/{id}', access: 'read',
        summary: 'One sensor, with its value',
        about: 'The sensor id goes in the path as it is (its slashes included), or URL-encoded.',
        call: (c) => ({ path: `/api/sensors${c.sensor.id}` }),
        pick: {
          python: 'print(data["name"], data["value"], data["unit"])',
          javascript: 'console.log(data.name, data.value, data.unit);',
          powershell: '"{0}: {1} {2}" -f $data.name, $data.value, $data.unit',
        },
        response: (c) => json({ id: c.sensor.id, name: c.sensor.name, hardware: c.sensor.hardware, hardwareType: 'Cpu', type: 'Temperature', unit: c.sensor.unit, category: 'cpu', kind: 'temperature', value: 61.25 }),
      },
      {
        id: 'values', method: 'GET', path: '/api/values', access: 'read',
        summary: 'Just the values',
        about: 'The most compact answer, for polling: `{ t, values: { id: value } }`, `t` = when they were read (Unix ms). Takes the same filters as `/api/sensors`; ids that don\'t exist answer `null`.',
        params: [['ids', 'comma-separated sensor ids'], ['category, kind, hardware, q, type', 'as for `/api/sensors`']],
        call: (c) => ({ path: '/api/values', query: { ids: c.sensors.join(',') } }),
        pick: {
          python: 'print(data["values"]["{sensor0}"])',
          javascript: 'console.log(data.values["{sensor0}"]);',
          powershell: '$data.values."{sensor0}"',
        },
        response: (c) => json({ t: 1793430459731, values: Object.fromEntries(c.sensors.map((id, i) => [id, [34.2, 61][i] ?? 0])) }),
      },
      {
        id: 'categories', method: 'GET', path: '/api/categories', access: 'read',
        summary: 'What this PC has, by category',
        about: 'Each category with how many sensors it has, its hardware, and the kinds of readings in it (with counts).',
        call: () => ({ path: '/api/categories' }),
        pick: {
          python: 'for c in data:\n    print(c["id"], c["count"], ", ".join(c["hardware"]))',
          javascript: 'for (const c of data) console.log(c.id, c.count, c.hardware.join(", "));',
          powershell: '$data | Format-Table id, count, hardware',
        },
        response: () => json([{ id: 'gpu', name: 'GPU', count: 41, hardware: ['NVIDIA GeForce RTX 5070'], kinds: [{ id: 'temperature', name: 'Temperature', count: 3 }, { id: 'load', name: 'Load', count: 9 }] }]),
      },
      {
        id: 'history', method: 'GET', path: '/api/history', access: 'read',
        summary: 'A sensor\'s history (up to 30 days)',
        about: 'Evenly spaced points with the average, lowest and highest reading in each step; `null` = no data then. `step` is in seconds, `start` in Unix ms.',
        params: [['sensor', 'the sensor id (required)'], ['days', '1 to 30 (default 1)']],
        call: (c) => ({ path: '/api/history', query: { sensor: c.sensor.id, days: 1 } }),
        pick: {
          python: 'peak = max(v for v in data["max"] if v is not None)\nprint("highest today:", peak)',
          javascript: 'console.log("highest today:", Math.max(...data.max.filter((v) => v != null)));',
          powershell: '"highest today: {0}" -f ($data.max | Where-Object { $_ -ne $null } | Measure-Object -Maximum).Maximum',
        },
        response: (c) => json({ sensor: c.sensor.id, enabled: true, step: 60, start: 1793344080000, avg: [48.1, 47.9], min: [45, 44.8], max: [55.3, 52.1] }),
      },
    ],
  },
  {
    title: 'Dashboard',
    about: 'The cards of this PC\'s dashboard with what they show right now, worked out the way the dashboard does: the unit the card shows (`unit`, e.g. GB when the sensor reports MB), the formatted `text`, the scale (`min`, `max`; `max` is the live total for a "used" reading, `null` = automatic), the threshold reached (`level`, with its colour; `null` = below them all), and its extra readings. `interval` is the card\'s own refresh rate in ms (`null` = the dashboard\'s). Card and section ids are shown in each card\'s API window in the dashboard.',
    endpoints: [
      {
        id: 'widget', method: 'GET', path: '/api/widgets/{id}', access: 'read',
        summary: 'One card, with its readings',
        params: [['layout', '`desktop` (default), `phone`, or the id or name of a saved layout']],
        call: (c) => ({ path: `/api/widgets/${c.widget.id}`, query: { layout: c.layout } }),
        pick: {
          python: 'w = data["widget"]\nprint(w["label"], w["text"])',
          javascript: 'const w = data.widget;\nconsole.log(w.label, w.text);',
          powershell: '"{0}: {1}" -f $data.widget.label, $data.widget.text',
        },
        response: (c) => json({
          t: 1793430459731,
          widget: {
            id: c.widget.id, section: null, type: 'gauge', label: c.widget.label, subtitle: c.sensor.hardware, interval: null,
            sensor: { id: '/amdcpu/0/load/0', name: 'CPU Total', hardware: c.sensor.hardware, unit: '%', available: true },
            value: 37.5, unit: '%', text: '38 %', min: 0, max: 100, level: { step: 'min', at: 30, color: '#22c55e' },
            extra: [{ sensor: { id: '/amdcpu/0/power/0', name: 'Package', hardware: c.sensor.hardware, unit: 'W', available: true }, label: '', value: 62.4, unit: 'W', text: '62.4 W' }],
          },
        }),
      },
      {
        id: 'section', method: 'GET', path: '/api/sections/{id}', access: 'read',
        summary: 'One section, with all its cards',
        params: [['layout', 'as above']],
        call: (c) => ({ path: `/api/sections/${c.section.id}`, query: { layout: c.layout } }),
        pick: {
          python: 'for w in data["section"]["widgets"]:\n    print(w["label"], w["text"])',
          javascript: 'for (const w of data.section.widgets) console.log(w.label, w.text);',
          powershell: '$data.section.widgets | Format-Table label, text',
        },
        response: (c) => json({ t: 1793430459731, section: { id: c.section.id, title: c.section.title, widgets: ['…cards, as above…'] } }),
      },
      {
        id: 'dashboard', method: 'GET', path: '/api/dashboard/readings', access: 'read',
        summary: 'The whole dashboard, with every reading',
        about: '`widgets` are the cards above the first section; then each section with its cards.',
        params: [['layout', 'as above']],
        call: (c) => ({ path: '/api/dashboard/readings', query: { layout: c.layout } }),
        pick: {
          python: 'for w in data["widgets"] + [w for s in data["sections"] for w in s["widgets"]]:\n    print(w["label"], w["text"])',
          javascript: 'for (const w of [...data.widgets, ...data.sections.flatMap((s) => s.widgets)]) console.log(w.label, w.text);',
          powershell: '@($data.widgets) + @($data.sections.widgets) | Format-Table label, text',
        },
        response: (c) => json({ pc: c.pc, layout: 'desktop', t: 1793430459731, widgets: ['…cards…'], sections: [{ id: c.section.id, title: c.section.title, widgets: ['…cards…'] }] }),
      },
      {
        id: 'layout', method: 'GET', path: '/api/dashboard', access: 'read',
        summary: 'The dashboard as stored (the layouts, not the readings)',
        about: '`{ rev, widgets (desktop layout), phone (null = same as desktop), look, saved, current }`; `rev` 0 = nothing saved yet. Each card is `{ id, sensor, type, w, h, label, min, max, thresholds, … }`, each section `{ id, kind: "section", title }`.',
        call: () => ({ path: '/api/dashboard' }),
        pick: {
          python: 'print(len(data["widgets"]), "cards and sections on the desktop layout")',
          javascript: 'console.log(data.widgets.length, "cards and sections on the desktop layout");',
          powershell: '"{0} cards and sections on the desktop layout" -f $data.widgets.Count',
        },
        response: () => json({ rev: 12, updated: 1793430459731, widgets: ['…'], phone: null, look: { bg: 'none', alpha: 100, round: 100, anim: 'smooth' }, saved: [], current: {} }),
      },
      {
        id: 'save-layout', method: 'PUT', path: '/api/dashboard', access: 'full',
        summary: 'Replace the dashboard',
        about: 'Body: `{ widgets, phone, look, saved, current }` as `GET /api/dashboard` returns them (max 2 MB). Answers `{ rev }`; open dashboards reload it at once. Read it first and send it back changed, so nothing else is lost.',
        call: () => ({ path: '/api/dashboard' }),
        // Read, change, send back: the whole dashboard goes in one piece.
        examples: (c, url) => ({
          curl: [
            `curl -H "${KEY_HEADER}: ${c.key}" "${url}" -o dashboard.json`,
            '# change dashboard.json, then send it back:',
            `curl -X PUT -H "${KEY_HEADER}: ${c.key}" -H "Content-Type: application/json" --data-binary @dashboard.json "${url}"`,
          ].join('\n'),
          powershell: [
            `$h = @{ "${KEY_HEADER}" = "${c.key}" }`,
            `$d = Invoke-RestMethod -Uri "${url}" -Headers $h`,
            '$d.widgets[0].label = "CPU"   # change something',
            `$data = Invoke-RestMethod -Method Put -Uri "${url}" -Headers $h -ContentType "application/json" -Body ($d | ConvertTo-Json -Depth 20)`,
            '"saved, revision {0}" -f $data.rev',
          ].join('\n'),
          python: [
            'import json, urllib.request',
            '',
            `h = {"${KEY_HEADER}": "${c.key}"}`,
            `with urllib.request.urlopen(urllib.request.Request("${url}", headers=h)) as r:`,
            '    d = json.load(r)',
            'd["widgets"][0]["label"] = "CPU"  # change something',
            `req = urllib.request.Request("${url}", method="PUT", data=json.dumps(d).encode(),`,
            '                             headers={**h, "Content-Type": "application/json"})',
            'with urllib.request.urlopen(req) as r:',
            '    data = json.load(r)',
            'print("saved, revision", data["rev"])',
          ].join('\n'),
          javascript: [
            `const h = { "${KEY_HEADER}": "${c.key}" };`,
            `const d = await (await fetch("${url}", { headers: h })).json();`,
            'd.widgets[0].label = "CPU";   // change something',
            `const r = await fetch("${url}", { method: "PUT", headers: { ...h, "Content-Type": "application/json" }, body: JSON.stringify(d) });`,
            'const data = await r.json();',
            'console.log("saved, revision", data.rev);',
          ].join('\n'),
          browser: null,
        }),
        response: () => json({ rev: 13 }),
      },
      {
        id: 'background', method: 'GET', path: '/api/dashboard/background', access: 'read',
        summary: 'The background picture',
        about: 'The picture itself, with its type in `Content-Type`; `404` when there is none. It shows when the dashboard\'s `look.bg` is `"image"`; `look.image` is its id (also the `ETag`).',
        call: () => ({ path: '/api/dashboard/background' }),
        noTry: true,
        examples: (c, url) => ({
          curl: `curl -H "${KEY_HEADER}: ${c.key}" "${url}" -o background`,
          powershell: `Invoke-WebRequest -Uri "${url}" -Headers @{ "${KEY_HEADER}" = "${c.key}" } -OutFile background`,
          python: [
            'import urllib.request',
            '',
            `req = urllib.request.Request("${url}", headers={"${KEY_HEADER}": "${c.key}"})`,
            'with urllib.request.urlopen(req) as r, open("background", "wb") as f:',
            '    f.write(r.read())',
          ].join('\n'),
          javascript: [
            `const r = await fetch("${url}", { headers: { "${KEY_HEADER}": "${c.key}" } });`,
            'const picture = await r.blob();   // r.headers.get("Content-Type") says which kind',
          ].join('\n'),
          browser: `${url}?key=${c.key}`,
        }),
        answer: 'the picture (JPEG, PNG, WebP, GIF or AVIF).',
      },
      {
        id: 'set-background', method: 'PUT', path: '/api/dashboard/background', access: 'full',
        summary: 'Set the background picture',
        about: 'Body: the picture (JPEG, PNG, WebP, GIF or AVIF, up to 8 MB) with its `Content-Type`. Answers `{ id }`. To show it, set `look.bg` to `"image"` and `look.image` to that id in the dashboard (`PUT /api/dashboard`; `look.fit` is `cover`, `contain` or `tile`, `look.dim` 0–80). `DELETE` on the same address removes the picture.',
        call: () => ({ path: '/api/dashboard/background' }),
        examples: (c, url) => ({
          curl: [
            `curl -X PUT -H "${KEY_HEADER}: ${c.key}" -H "Content-Type: image/jpeg" --data-binary @picture.jpg "${url}"`,
            `curl -X DELETE -H "${KEY_HEADER}: ${c.key}" "${url}"   # removes it`,
          ].join('\n'),
          powershell: [
            `$data = Invoke-RestMethod -Method Put -Uri "${url}" -Headers @{ "${KEY_HEADER}" = "${c.key}" } -ContentType "image/jpeg" -InFile picture.jpg`,
            '"picture id: {0}" -f $data.id',
          ].join('\n'),
          python: [
            'import json, urllib.request',
            '',
            'with open("picture.jpg", "rb") as f:',
            `    req = urllib.request.Request("${url}", method="PUT", data=f.read(),`,
            `                                 headers={"${KEY_HEADER}": "${c.key}", "Content-Type": "image/jpeg"})`,
            'with urllib.request.urlopen(req) as r:',
            '    data = json.load(r)',
            'print("picture id:", data["id"])',
          ].join('\n'),
          javascript: [
            '// picture: a File from <input type="file">, or any Blob',
            `const r = await fetch("${url}", { method: "PUT", headers: { "${KEY_HEADER}": "${c.key}", "Content-Type": picture.type }, body: picture });`,
            'const data = await r.json();',
            'console.log("picture id:", data.id);',
          ].join('\n'),
          browser: null,
        }),
        response: () => json({ id: '5d41402abc4b2a76' }),
      },
    ],
  },
  {
    title: 'PCs',
    about: 'The other SYSCOM PCs, as the dashboard knows them.',
    endpoints: [
      {
        id: 'devices', method: 'GET', path: '/api/devices', access: 'read',
        summary: 'The PCs this dashboard shows',
        about: '`{ rev, devices: [ { id, name, url, httpsPort } ] }`; `url` is the PC\'s plain HTTP address (`null` for this PC itself); the dashboard swipes through them in this order.',
        call: () => ({ path: '/api/devices' }),
        pick: { python: 'for d in data["devices"]:\n    print(d["name"], d["url"])', javascript: 'for (const d of data.devices) console.log(d.name, d.url);', powershell: '$data.devices | Format-Table name, url' },
        response: (c) => json({ rev: 3, devices: [{ id: 'a1b2', name: c.pc, url: null, httpsPort: 47812 }, { id: 'c3d4', name: 'HP-NAS', url: 'http://192.168.1.30:47810', httpsPort: 47812 }] }),
      },
      {
        id: 'save-devices', method: 'PUT', path: '/api/devices', access: 'full',
        summary: 'Change the PCs this dashboard shows',
        about: 'Body: `{ devices: [ { id, name, url, httpsPort } ] }` (up to 64), the whole list in the order to show. Answers `{ rev }`; open dashboards pick it up at once. Each needs an `id` or a `url`; a PC\'s `id` and `httpsPort` are in its `/api/info` (or `GET /api/peers`).',
        call: () => ({ path: '/api/devices' }),
        examples: (c, url) => ({
          curl: [
            `curl -H "${KEY_HEADER}: ${c.key}" "${url}" -o devices.json`,
            '# change devices.json, then send it back:',
            `curl -X PUT -H "${KEY_HEADER}: ${c.key}" -H "Content-Type: application/json" --data-binary @devices.json "${url}"`,
          ].join('\n'),
          powershell: [
            `$h = @{ "${KEY_HEADER}" = "${c.key}" }`,
            `$d = Invoke-RestMethod -Uri "${url}" -Headers $h`,
            '$d.devices = @($d.devices | Where-Object name -ne "HP-NAS")   # e.g. remove a PC',
            `$data = Invoke-RestMethod -Method Put -Uri "${url}" -Headers $h -ContentType "application/json" -Body (@{ devices = $d.devices } | ConvertTo-Json -Depth 5)`,
            '"saved, revision {0}" -f $data.rev',
          ].join('\n'),
          python: [
            'import json, urllib.request',
            '',
            `h = {"${KEY_HEADER}": "${c.key}"}`,
            `with urllib.request.urlopen(urllib.request.Request("${url}", headers=h)) as r:`,
            '    devices = json.load(r)["devices"]',
            'devices = [d for d in devices if d["name"] != "HP-NAS"]  # e.g. remove a PC',
            `req = urllib.request.Request("${url}", method="PUT", data=json.dumps({"devices": devices}).encode(),`,
            '                             headers={**h, "Content-Type": "application/json"})',
            'with urllib.request.urlopen(req) as r:',
            '    data = json.load(r)',
            'print("saved, revision", data["rev"])',
          ].join('\n'),
          javascript: [
            `const h = { "${KEY_HEADER}": "${c.key}" };`,
            `const { devices } = await (await fetch("${url}", { headers: h })).json();`,
            'const keep = devices.filter((d) => d.name !== "HP-NAS");   // e.g. remove a PC',
            `const r = await fetch("${url}", { method: "PUT", headers: { ...h, "Content-Type": "application/json" }, body: JSON.stringify({ devices: keep }) });`,
            'const data = await r.json();',
            'console.log("saved, revision", data.rev);',
          ].join('\n'),
          browser: null,
        }),
        response: () => json({ rev: 4 }),
      },
      {
        id: 'peers', method: 'GET', path: '/api/peers', access: 'read',
        summary: 'SYSCOM PCs heard on the network',
        call: () => ({ path: '/api/peers' }),
        pick: { python: 'for p in data:\n    print(p["name"], p["url"])', javascript: 'for (const p of data) console.log(p.name, p.url);', powershell: '$data | Format-Table name, url' },
        response: () => json([{ id: 'c3d4', name: 'HP-NAS', url: 'http://192.168.1.30:47810', httpsUrl: 'https://192.168.1.30:47812' }]),
      },
      {
        id: 'info', method: 'GET', path: '/api/info', access: 'none',
        summary: 'This PC\'s name and version (no key needed)',
        call: () => ({ path: '/api/info' }),
        pick: { python: 'print(data["name"], data["version"])', javascript: 'console.log(data.name, data.version);', powershell: '"{0} runs SYSCOM {1}" -f $data.name, $data.version' },
        response: (c) => json({ id: '9f1c…', name: c.pc, version: '0.18.0', os: 'Microsoft Windows NT 10.0.26100.0', minIntervalMs: 500, port: 47810, httpsPort: 47812, sources: ['LibreHardwareMonitor', 'Drive space', 'Frame rate'] }),
      },
    ],
  },
];

export const ENDPOINTS = GROUPS.flatMap((g) => g.endpoints);

/** A sensor's category and kind, as the PC works them out (src/Syscom.Server/Api/Readings.cs). */
export function categoryOf(s) {
  const t = s?.hardwareType || '';
  if (t === 'Cpu') return 'cpu';
  if (t.startsWith('Gpu')) return 'gpu';
  if (t === 'Game') return 'game';
  if (t === 'Memory') return 'memory';
  if (t === 'Storage' || t === 'Drive') return 'storage';
  if (t === 'Network') return 'network';
  if (['Motherboard', 'SuperIO', 'EmbeddedController', 'Cooler', 'Psu', 'Battery'].includes(t)) return 'motherboard';
  return 'other';
}
export function kindOf(s) {
  if (s.unit === '%' && /\bused\b|\bspace\b/i.test(s.name)) return 'size';
  if (s.type === 'Load' && /\bpower\b/i.test(s.name)) return 'power';
  const kinds = {
    temperature: ['Temperature'], load: ['Load', 'Level', 'Factor'], clock: ['Clock', 'Frequency'], power: ['Power', 'Voltage', 'Current', 'Energy'],
    size: ['Data', 'SmallData'], fan: ['Fan', 'Control', 'Flow'], throughput: ['Throughput'], fps: ['Fps', 'Frametime'],
  };
  return Object.keys(kinds).find((k) => kinds[k].includes(s.type)) || 'other';
}
export const endpoint = (id) => ENDPOINTS.find((e) => e.id === id);

export const ACCESS = {
  none: 'no key',
  read: 'read key',
  full: 'full-access key',
  session: 'signed-in dashboard',
};

/** The request URL of an example: base + path + query (the key stays in a header). */
export function urlOf(ctx, call) {
  const q = Object.entries(call.query || {}).filter(([, v]) => v != null && v !== '')
    .map(([k, v]) => `${k}=${encodeURIComponent(v).replace(/%2C/g, ',').replace(/%2F/g, '/')}`).join('&');
  return `${ctx.base}${call.path}${q ? `?${q}` : ''}`;
}

const fill = (s, ctx) => s.replaceAll('{sensor0}', ctx.sensors[0] || ctx.sensor.id);

/** The same call in each language: { curl, powershell, python, javascript, browser }. */
export function examples(ep, ctx) {
  const url = urlOf(ctx, ep.call(ctx));
  if (ep.examples) return ep.examples(ctx, url);
  const key = ep.access !== 'none';
  const pick = Object.fromEntries(Object.entries(ep.pick || {}).map(([k, v]) => [k, fill(v, ctx)]));
  return {
    curl: `curl${key ? ` -H "${KEY_HEADER}: ${ctx.key}"` : ''} "${url}"`,
    powershell: [
      `$data = Invoke-RestMethod -Uri "${url}"${key ? ` -Headers @{ "${KEY_HEADER}" = "${ctx.key}" }` : ''}`,
      pick.powershell,
    ].filter(Boolean).join('\n'),
    python: [
      'import json, urllib.request',
      '',
      `req = urllib.request.Request("${url}"${key ? `, headers={"${KEY_HEADER}": "${ctx.key}"}` : ''})`,
      'with urllib.request.urlopen(req) as r:',
      '    data = json.load(r)',
      pick.python,
    ].filter(Boolean).join('\n'),
    javascript: [
      `const r = await fetch("${url}"${key ? `, { headers: { "${KEY_HEADER}": "${ctx.key}" } }` : ''});`,
      'const data = await r.json();',
      pick.javascript,
    ].filter(Boolean).join('\n'),
    // GET requests also take the key in the address: handy in a browser, but it ends up in histories and logs.
    browser: key ? `${url}${url.includes('?') ? '&' : '?'}key=${ctx.key}` : url,
  };
}

/** Live readings over a WebSocket, the way the dashboard gets them. */
export function streamExample(ctx) {
  const ws = ctx.base.replace(/^http/, 'ws');
  return {
    javascript: [
      `const ws = new WebSocket("${ws}/ws?key=${ctx.key}");`,
      'let order = [];',
      'ws.onopen = () => ws.send(JSON.stringify({',
      `  type: "subscribe", interval: 1000, sensors: ${JSON.stringify(ctx.sensors)},`,
      '}));',
      'ws.onmessage = (e) => {',
      '  const m = JSON.parse(e.data);',
      '  if (m.type === "subscribed") order = m.sensors;',
      '  if (m.type === "data") console.log(Object.fromEntries(order.map((id, i) => [id, m.v[i]])));',
      '};',
    ].join('\n'),
    python: [
      '# pip install websockets',
      'import asyncio, json, websockets',
      '',
      'async def main():',
      `    async with websockets.connect("${ws}/ws?key=${ctx.key}") as ws:`,
      `        await ws.send(json.dumps({"type": "subscribe", "interval": 1000, "sensors": ${JSON.stringify(ctx.sensors)}}))`,
      '        order = []',
      '        async for raw in ws:',
      '            m = json.loads(raw)',
      '            if m["type"] == "subscribed":',
      '                order = m["sensors"]',
      '            elif m["type"] == "data":',
      '                print(dict(zip(order, m["v"])))',
      '',
      'asyncio.run(main())',
    ].join('\n'),
  };
}

/** The introduction, as [title, paragraphs]; `code` in backticks, **bold** for emphasis. */
export const INTRO = [
  ['What it is', [
    'Everything the dashboard shows can be read by scripts and other apps: every sensor with its live value, the history, and the dashboard itself, card by card. Every PC running SYSCOM answers on its own address, e.g. `http://192.168.1.20:47810` (and `https://…:47812` when HTTPS is on). Answers are JSON.',
    'The easy way in: build what you want to see in the dashboard, then open a card\'s **API** window (✎ edit mode → the `</>` button on the card, a section or the dashboard). It shows the exact address and ready-to-run examples for that card, filled in with this PC\'s address and ids.',
  ]],
  ['Keys', [
    'Create a key in the dashboard: Settings → **API keys** (or any API window). A **read** key can read everything below; a **full** key can also change the dashboard and the device list. The key is shown once: copy it then. Revoke a key there to stop it working at once. Keys are kept per PC.',
    `Send the key in a header: \`${KEY_HEADER}: syscom_…\` or \`Authorization: Bearer syscom_…\`. For a quick look in a browser, GET requests and WebSockets also take \`?key=syscom_…\` in the address (it then ends up in browser history and logs, so prefer the header in scripts).`,
    'A missing, wrong or revoked key answers `401`; a read key trying to change something answers `401` too. Creating and revoking keys needs the dashboard (password sign-in): keys can\'t make more keys.',
  ]],
  ['Freshness', [
    'Readings are at most about a second old. When no dashboard is open, the first call may take up to a second while the PC takes a fresh reading; the PC then keeps reading every second for a minute after your last call. For faster or continuous updates, use the WebSocket stream below.',
  ]],
];

export const STREAM_ABOUT = [
  'For live readings, open a WebSocket to `/ws?key=…` (the key in the address: browsers can\'t send headers on WebSockets) and say which sensors you want and how often (ms, 500 to 60000). The PC first sends its sensor list (`catalog`), confirms your subscription (`subscribed`, with the order of the values), then sends a `data` frame every interval: `{ "type": "data", "t": …, "v": [ … ] }`, values in that order.',
  'Several rates on one connection: `{ "type": "subscribe", "streams": [ { "id": 1, "interval": 500, "sensors": [ … ] }, { "id": 2, "interval": 5000, "sensors": [ … ] } ] }`; each `data` frame then names its stream in `"s"`. Omit `sensors` for every sensor. A bad key closes the socket with code 4401.',
];
