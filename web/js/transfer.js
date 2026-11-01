// Layout files: export one layout (desktop or phone) of one PC, and import a layout into any PC, matching each
// reading to that PC's sensors. Plain functions, no DOM.
import { isSection, extrasOf } from './widgets.js';

export const FILE_VERSION = 2;

/** Sensor ids a layout uses (cards, extra readings, live scale totals). */
function sensorIds(items) {
  const ids = new Set();
  for (const w of items) {
    if (isSection(w)) continue;
    if (w.sensor) ids.add(w.sensor);
    for (const x of extrasOf(w)) ids.add(x.sensor);
    for (const id of Array.isArray(w.maxFrom) ? w.maxFrom : []) ids.add(id);
  }
  return ids;
}

/**
 * A layout file: { app, kind, version, exported, source: { name, serverId }, layout: 'desktop'|'phone', name?,
 * items, sensors: { id: { name, hardware, hardwareType, type, unit } } }. The sensors' descriptions let another PC
 * find its own matching readings.
 */
export function exportLayout({ dev, which, items, name = null, catalog = [] }) {
  const byId = new Map(catalog.map((s) => [s.id, s]));
  const sensors = {};
  for (const id of sensorIds(items)) {
    const s = byId.get(id);
    if (s) sensors[id] = { name: s.name, hardware: s.hardware, hardwareType: s.hardwareType, type: s.type, unit: s.unit };
  }
  return {
    app: 'syscom', kind: 'layout', version: FILE_VERSION, exported: new Date().toISOString(),
    source: { name: dev.name, serverId: dev.serverId }, layout: which, ...(name ? { name } : {}),
    items: structuredClone(items), sensors,
  };
}

/** A safe file name: "syscom-LEVI-ASUS-PC-desktop.json". */
export function fileName(...parts) {
  return 'syscom-' + parts.filter(Boolean).join('-').replace(/[^\w.-]+/g, '_').replace(/_+/g, '_') + '.json';
}

/**
 * Reads a layout file for one PC's layout. Accepts this version's files and the older whole-browser backups
 * ({ devices: [...] }, which carried every PC's layouts): from those it takes the PC with the same server id, else
 * the first one. Returns { items, sensors (descriptions or null), from (source PC name), which (layout in the file) }.
 */
export function readLayoutFile(data, { serverId, which }) {
  if (!data || data.app !== 'syscom') throw new Error('Not a SYSCOM layout file.');
  if (Array.isArray(data.items)) {
    return { items: data.items, sensors: data.sensors || null, from: data.source?.name || 'another PC', which: data.layout || 'desktop', name: data.name || null };
  }
  if (Array.isArray(data.devices) && data.devices.length) {
    const d = data.devices.find((x) => serverId && x.serverId === serverId) || data.devices[0];
    const desktop = d.layouts?.desktop ?? d.widgets;
    const phone = d.layouts?.phone;
    const items = which === 'phone' && Array.isArray(phone) ? phone : desktop;
    if (!Array.isArray(items)) throw new Error('That file has no layout in it.');
    return { items, sensors: null, from: d.name || 'another PC', which: items === phone ? 'phone' : 'desktop', name: null };
  }
  throw new Error('That file has no layout in it.');
}

// ---- matching readings from another PC to this one's sensors

/** The hardware family an id or description belongs to, so a CPU reading only matches a CPU reading. */
function family(hardwareType) {
  const t = hardwareType || '';
  if (t.startsWith('Gpu')) return 'gpu';
  if (['Storage', 'Drive'].includes(t)) return t.toLowerCase();
  if (['Motherboard', 'SuperIO', 'EmbeddedController'].includes(t)) return 'board';
  return t.toLowerCase();
}

/** What an old file (no sensor descriptions) tells about a sensor from its id alone: "/amdcpu/0/load/1". */
function describeId(id) {
  const parts = id.split('/').filter(Boolean);
  if (parts[0] === 'frames' || parts[0] === 'drive') return null;   // same ids on every PC
  const hw = parts[0] || '';
  const hardwareType = /cpu/.test(hw) ? 'Cpu' : /^gpu/.test(hw) ? 'Gpu' : /^(ram|memory)/.test(hw) ? 'Memory'
    : /^(nvme|hdd|ssd|storage)/.test(hw) ? 'Storage' : /^nic/.test(hw) ? 'Network'
      : /^(lpc|mainboard|motherboard|ec)/.test(hw) ? 'Motherboard' : hw;
  const typeSeg = parts.find((p, i) => i > 0 && /^[a-z]+$/.test(p) && p !== 'percent');
  const type = typeSeg ? typeSeg.replace(/^./, (c) => c.toUpperCase()).replace('Smalldata', 'SmallData') : null;
  const index = Number(parts[parts.length - 1]);
  return { hardwareType, type, index: Number.isFinite(index) ? index : 0, idParts: parts };
}

const words = (s) => String(s || '').toLowerCase().split(/[^a-z0-9]+/).filter(Boolean);
function similarity(a, b) {
  const A = new Set(words(a)), B = new Set(words(b));
  if (!A.size || !B.size) return 0;
  let common = 0;
  for (const w of A) if (B.has(w)) common++;
  return common / Math.max(A.size, B.size);
}

/**
 * Finds this PC's sensor for a sensor of another PC. Same id first (frame rate and drive letters are the same
 * everywhere; so is everything when it's the same PC), then the same kind of reading on the same kind of hardware
 * with the same name, then the closest name.
 */
export function makeMatcher(catalog) {
  const byId = new Map(catalog.map((s) => [s.id, s]));
  const memo = new Map();
  function match(id, meta) {
    if (memo.has(id)) return memo.get(id);
    let hit = null;
    const own = byId.get(id);
    if (own && (!meta || (own.type === meta.type && family(own.hardwareType) === family(meta.hardwareType)))) hit = id;
    else if (id.endsWith('/percent') || id.endsWith('/total') || id.endsWith('/free')) {
      // A reading the server derives from another one: match that one, then take its counterpart.
      const cut = id.lastIndexOf('/');
      const base = match(id.slice(0, cut), null);
      if (base && byId.has(base + id.slice(cut))) hit = base + id.slice(cut);
    }
    // Drive letters and the frame counter have the same ids on every PC: another drive is never a match (E: would
    // otherwise become whichever drive comes first, on every card).
    if (!hit && !/^\/(drive|frames)\//.test(id)) {
      const d = meta || describeId(id);
      if (d) {
        const pool = catalog.filter((s) => family(s.hardwareType) === family(d.hardwareType) && (!d.type || s.type === d.type));
        if (meta) {
          hit = (pool.find((s) => s.name === meta.name && s.hardware === meta.hardware)
            || pool.find((s) => s.name.toLowerCase() === String(meta.name).toLowerCase())
            || bestBy(pool, (s) => similarity(s.name, meta.name), 0.5))?.id || null;
        } else {
          // Old files: the same position among that device's readings of this type (e.g. load #0 = total load).
          const sameShape = pool.filter((s) => {
            const p = s.id.split('/').filter(Boolean);
            return p.length === d.idParts.length && Number(p[p.length - 1]) === d.index;
          });
          hit = (sameShape[0] || null)?.id || null;
        }
      }
    }
    memo.set(id, hit);
    return hit;
  }
  return match;
}

function bestBy(list, score, min) {
  let best = null, top = min;
  for (const x of list) { const v = score(x); if (v > top) { top = v; best = x; } }
  return best;
}

/**
 * Points a layout's readings at this PC's sensors. Cards whose reading isn't found keep the card (it shows
 * "unavailable" and can be edited); extra readings that aren't found are left out.
 * Returns { items, matched, missing: [descriptions] }.
 */
export function remapLayout(items, sensors, catalog) {
  const match = makeMatcher(catalog);
  const describe = (id) => sensors?.[id] ? `${sensors[id].name} (${sensors[id].hardware})` : id;
  const missing = new Set();
  let matched = 0;
  const out = structuredClone(items).map((w) => {
    if (isSection(w) || !w.sensor) return w;
    const hit = match(w.sensor, sensors?.[w.sensor] || null);
    if (hit) { if (hit !== w.sensor) matched++; w.sensor = hit; } else missing.add(describe(w.sensor));
    if (Array.isArray(w.extra)) {
      w.extra = extrasOf(w).map((x) => {
        const xh = match(x.sensor, sensors?.[x.sensor] || null);
        if (!xh) { missing.add(describe(x.sensor)); return null; }
        return { ...x, sensor: xh };
      }).filter(Boolean);
      if (!w.extra.length) delete w.extra;
    }
    if (Array.isArray(w.maxFrom) && w.maxFrom.length) {
      const mf = w.maxFrom.map((id) => match(id, sensors?.[id] || null));
      if (mf.every(Boolean)) w.maxFrom = mf; else delete w.maxFrom;   // falls back to the detected total
    }
    return w;
  });
  return { items: out, matched, missing: [...missing] };
}
