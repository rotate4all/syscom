// The on-screen display (OSD): readings the tray app draws over games on the PC's own screen, for people without a
// second screen or a phone to put the dashboard on. It's each PC's third layout (besides desktop and phone): a list
// of readings, each with its place on the screen and whether it goes under or beside the one before it there, and
// the OSD's look (font, size, colours, transparency, shortcut). The server formats the readings the same way for the
// tray app (src/Syscom.Server/Osd/OsdReadings.cs) and the tray app lays them out like the preview here
// (src/Syscom.Tray/Osd/OsdRenderer.cs).
import { COLORS, NUMBER_SIZE, defaultsFor, displayUnit, unitFactor, format, colorFor } from './widgets.js';

/** Places on the screen: [key, name]. The middle of the screen is left for the game. */
export const POSITIONS = [
  ['tl', 'Top left'], ['tc', 'Top center'], ['tr', 'Top right'],
  ['ml', 'Middle left'], ['mr', 'Middle right'],
  ['bl', 'Bottom left'], ['bc', 'Bottom center'], ['br', 'Bottom right'],
];
export const positionName = (pos) => POSITIONS.find(([k]) => k === pos)?.[1] || 'Top left';

export const OUTLINES = [['outline', 'Outline'], ['shadow', 'Shadow'], ['none', 'None']];
export const OSD_INTERVALS = [500, 1000, 2000, 3000, 5000];
export const DEFAULT_HOTKEY = 'Ctrl+Shift+S';
export const MAX_OSD_ITEMS = 40;
export const MAX_OSD_LABEL = 40;
/** Fonts every Windows PC has, for when the PC's own list isn't known (its tray app isn't running). */
export const COMMON_FONTS = ['Segoe UI', 'Segoe UI Semibold', 'Bahnschrift', 'Consolas', 'Cascadia Mono', 'Arial', 'Calibri', 'Tahoma', 'Verdana', 'Trebuchet MS', 'Impact', 'Courier New'];

export const OSD_LOOK_DEFAULTS = Object.freeze({
  font: 'Segoe UI',
  bold: true,
  size: 18,               // px at 100 % display scaling
  color: '#ffffff',       // numbers
  labelColor: '#9cc3ff',  // labels and units
  transparency: 0,        // 0 (solid) … 90 %
  backdrop: 40,           // opacity of the plate behind each group, 0 = none
  backdropColor: '#000000',
  outline: 'outline',     // outline | shadow | none
  margin: 16,             // px from the screen's edges
  screen: null,           // Windows device name of the screen; null = the main one
  hotkey: DEFAULT_HOTKEY, // '' = no shortcut
  interval: 1000,
});

const hex = (v) => (typeof v === 'string' && /^#[0-9a-f]{6}$/i.test(v) ? v.toLowerCase() : null);
const clamp = (v, lo, hi, d) => (Number.isFinite(Number(v)) && v !== null && v !== '' ? Math.min(hi, Math.max(lo, Math.round(Number(v)))) : d);

export function normalizeOsdLook(l) {
  const x = l && typeof l === 'object' ? l : {};
  const d = OSD_LOOK_DEFAULTS;
  const font = typeof x.font === 'string' && x.font.trim() && x.font.length <= 64 ? x.font.trim() : d.font;
  return {
    font,
    bold: typeof x.bold === 'boolean' ? x.bold : d.bold,
    size: Number.isFinite(Number(x.size)) && x.size !== null ? Math.min(72, Math.max(8, Math.round(Number(x.size) * 10) / 10)) : d.size,
    color: hex(x.color) || d.color,
    labelColor: hex(x.labelColor) || d.labelColor,
    transparency: clamp(x.transparency, 0, 90, d.transparency),
    backdrop: clamp(x.backdrop, 0, 100, d.backdrop),
    backdropColor: hex(x.backdropColor) || d.backdropColor,
    outline: OUTLINES.some(([k]) => k === x.outline) ? x.outline : d.outline,
    margin: clamp(x.margin, 0, 400, d.margin),
    screen: typeof x.screen === 'string' && x.screen ? x.screen.slice(0, 64) : null,
    hotkey: typeof x.hotkey === 'string' ? tidyHotkey(x.hotkey.slice(0, 64)) : d.hotkey,
    interval: clamp(x.interval, 500, 10000, d.interval),
  };
}

/**
 * A reading of the OSD from storage: { id, sensor, label?, pos, stack?, unit?, decimals, thresholds?, color?, size?,
 * hideUnit? }. No label = the sensor's name; an empty label = none. Cards (from a desktop or phone layout file
 * imported into the OSD) become readings too; sections are left out.
 */
export function normalizeOsdItem(x) {
  if (!x || typeof x !== 'object' || x.kind === 'section' || typeof x.sensor !== 'string' || !x.sensor) return null;
  const out = { ...(typeof x.id === 'string' && x.id ? { id: x.id } : {}), sensor: x.sensor };
  if (typeof x.label === 'string') out.label = x.label.trim().slice(0, MAX_OSD_LABEL);
  out.pos = POSITIONS.some(([k]) => k === x.pos) ? x.pos : 'tl';
  if (x.stack === 'beside') out.stack = 'beside';
  if (typeof x.unit === 'string' && x.unit) out.unit = x.unit;
  out.decimals = clamp(x.decimals, 0, 6, 0);
  if (Array.isArray(x.thresholds)) {
    const fallback = [COLORS.min, COLORS.med, COLORS.max];
    out.thresholds = [0, 1, 2].map((i) => {
      const t = x.thresholds[i] || {};
      const v = t.value === '' || t.value == null || Number.isNaN(Number(t.value)) ? null : Number(t.value);
      return { value: v, color: hex(t.color) || fallback[i] };
    });
  }
  if (hex(x.color)) out.color = hex(x.color);
  if (NUMBER_SIZE.some(([k]) => k === x.size) && x.size !== 'm') out.size = x.size;
  if (x.hideUnit === true) out.hideUnit = true;
  return out;
}

export function normalizeOsdItems(items) {
  return (Array.isArray(items) ? items : []).map(normalizeOsdItem).filter(Boolean).slice(0, MAX_OSD_ITEMS);
}

/** Sensible settings for a reading added to the OSD. */
export function osdDefaultsFor(sensor, catalog = []) {
  const w = defaultsFor(sensor, catalog);
  const item = { sensor: sensor.id, label: sensor.name, pos: 'tl', decimals: w.decimals ?? 0, thresholds: w.thresholds.map((t) => ({ ...t })) };
  // Normal readings show in the OSD's own colour: only warnings get one. Frame rates keep theirs (low is the bad end).
  if (sensor.type !== 'Fps') item.thresholds[0].value = null;
  // VRAM is reported in MB; GB reads better on a small OSD.
  if (sensor.unit === 'MB' && sensor.type === 'SmallData') { item.unit = 'GB'; item.decimals = 1; }
  return item;
}

/**
 * A first OSD for a PC: CPU and GPU temperature, load and power on a line each, VRAM and RAM in use, top left;
 * frame rate (bigger), native frame rate (with frame generation) and frame time, top right.
 */
export function suggestOsd(catalog) {
  const out = [];
  const find = (pred) => catalog.find(pred);
  const add = (s, o) => { if (s && !out.some((x) => x.sensor === s.id)) out.push({ ...osdDefaultsFor(s, catalog), ...o }); return s; };
  const isCpu = (s) => s.hardwareType === 'Cpu';
  const isGpu = (s) => s.hardwareType.startsWith('Gpu');
  const named = (re) => (s) => re.test(s.name);
  const line = (label, readings) => {
    const [first, ...rest] = readings.filter(Boolean);
    if (!first) return;
    add(first, { label, pos: 'tl' });
    for (const s of rest) add(s, { label: '', pos: 'tl', stack: 'beside' });
  };

  line('CPU', [
    find((s) => isCpu(s) && s.type === 'Temperature' && named(/package|tctl|tdie|core max/i)(s)) || find((s) => isCpu(s) && s.type === 'Temperature'),
    find((s) => isCpu(s) && s.type === 'Load' && named(/total/i)(s)),
    find((s) => isCpu(s) && s.type === 'Power' && named(/package|cpu power|ppt/i)(s)),
  ]);
  line('GPU', [
    find((s) => isGpu(s) && s.type === 'Temperature' && named(/core/i)(s)) || find((s) => isGpu(s) && s.type === 'Temperature'),
    find((s) => isGpu(s) && s.type === 'Load' && named(/core/i)(s)),
    find((s) => isGpu(s) && s.type === 'Power' && named(/package|power|board|total/i)(s)),
  ]);
  const vram = find((s) => isGpu(s) && /^gpu memory used$/i.test(s.name)) || find((s) => isGpu(s) && /dedicated memory used/i.test(s.name));
  add(vram, { label: 'VRAM', pos: 'tl', unit: 'GB', decimals: 1, thresholds: undefined });
  add(find((s) => s.hardwareType === 'Memory' && s.type === 'Data' && /^memory used$/i.test(s.name)), { label: 'RAM', pos: 'tl', decimals: 1, thresholds: undefined });

  const fps = add(find((s) => s.id === '/frames/fps') || find((s) => s.type === 'Fps'), { label: 'FPS', pos: 'tr', size: 'l', hideUnit: true });
  if (fps?.id === '/frames/fps') add(find((s) => s.id === '/frames/fps-native'), { label: 'Native', pos: 'tr', hideUnit: true });
  add(find((s) => s.id === '/frames/frametime'), { label: 'Frame time', pos: 'tr', decimals: 1 });
  return out.map((x) => normalizeOsdItem(x));
}

/** What a reading shows now: { label, number, unit, color (null = the OSD's) }. */
export function osdText(item, sensor, value) {
  const unit = displayUnit(item, sensor);
  const v = value == null || Number.isNaN(value) ? null : value * unitFactor(sensor?.unit, unit);
  const f = format(v, unit, item.decimals ?? 0);
  return {
    label: typeof item.label === 'string' ? item.label : sensor?.name || 'Sensor',
    number: f.n,
    unit: item.hideUnit || (v == null && unit === 'B/s') ? '' : f.u || '',
    color: colorFor(item, v) || item.color || null,
  };
}

/** The readings by place, row by row (a reading "beside" the one before it in its place joins that one's row). */
export function osdGroups(items) {
  const byPos = new Map();
  for (const x of items) {
    if (!byPos.has(x.pos)) byPos.set(x.pos, []);
    const rows = byPos.get(x.pos);
    if (x.stack === 'beside' && rows.length) rows[rows.length - 1].push(x);
    else rows.push([x]);
  }
  return POSITIONS.filter(([k]) => byPos.has(k)).map(([pos]) => ({ pos, rows: byPos.get(pos) }));
}

/**
 * Takes a reading out of the list. When it started a row, the reading beside it (if any) starts the row instead,
 * so it doesn't slip up onto the row above.
 */
export function removeOsdItem(items, item) {
  const at = items.indexOf(item);
  if (at < 0) return items;
  const next = items.slice(at + 1).find((x) => x.pos === item.pos);
  if (item.stack !== 'beside' && next?.stack === 'beside') delete next.stack;
  return items.filter((x) => x !== item);
}

/** The text and the plate of a group, as the tray app draws them (app.css has the rest). */
export function osdGroupStyle(look) {
  const shadow = look.outline === 'outline'
    ? [[-1, -1], [1, -1], [-1, 1], [1, 1], [0, -1.3], [0, 1.3], [-1.3, 0], [1.3, 0]].map(([x, y]) => `${x * 0.06}em ${y * 0.06}em 0 rgb(0 0 0 / .88)`).join(', ')
    : look.outline === 'shadow' ? '.07em .07em 0 rgb(0 0 0 / .67)' : 'none';
  const a = look.backdrop / 100;
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(look.backdropColor.slice(i, i + 2), 16));
  return {
    fontFamily: `"${look.font.replace(/["\\]/g, '')}", "Segoe UI", system-ui, sans-serif`,
    fontWeight: look.bold ? '700' : '400',
    color: look.color,
    opacity: String((100 - look.transparency) / 100),
    textShadow: shadow,
    background: a > 0 ? `rgb(${r} ${g} ${b} / ${a})` : 'transparent',
    padding: a > 0 ? '.45em' : look.outline === 'none' ? '.1em' : '.2em',
  };
}

/**
 * Draws the OSD preview into `screen` (an .osd-screen element): one table per place, three cells per reading
 * (label, number, unit). Returns a Map of item id → { item, cells: { l, n, u } } to update the readings in.
 */
export function renderOsdPreview(screen, items, look, sensorOf) {
  screen.querySelectorAll('.osd-g').forEach((g) => g.remove());
  const els = new Map();
  const style = osdGroupStyle(look);
  for (const group of osdGroups(items)) {
    const g = document.createElement('div');
    g.className = 'osd-g';
    g.dataset.pos = group.pos;
    Object.assign(g.style, style);
    g.style.setProperty('--label', look.labelColor);
    const cols = Math.max(...group.rows.map((r) => r.length));
    group.rows.forEach((row, r) => {
      row.forEach((item, k) => {
        const e = document.createElement('span');
        e.className = 'osd-e';
        e.dataset.id = item.id;
        const scale = NUMBER_SIZE.find(([s]) => s === item.size)?.[2] ?? 1;
        const cells = {};
        for (const [key, col] of [['l', 1], ['n', 2], ['u', 3]]) {
          const c = document.createElement('span');
          c.className = `osd-${key}${k === cols - 1 ? ' last' : ''}`;
          c.style.gridRow = String(r + 1);
          c.style.gridColumn = String(k * 3 + col);
          c.style.fontSize = `${(key === 'u' ? 0.8 : 1) * scale}em`;
          cells[key] = c;
          e.append(c);
        }
        g.append(e);
        els.set(item.id, { item, cells });
        paintOsdItem(els.get(item.id), sensorOf(item.sensor), null, look);
      });
    });
    screen.append(g);
  }
  return els;
}

/** Puts a reading's current value in its cells. */
export function paintOsdItem(el, sensor, value, look) {
  const t = osdText(el.item, sensor, value);
  const set = (c, text) => { if (c.textContent !== text) c.textContent = text; };
  set(el.cells.l, t.label);
  set(el.cells.n, t.number);
  set(el.cells.u, t.unit);
  el.cells.n.style.color = t.color || look.color;
}

// ---- the shortcut: written like "Ctrl+Shift+S" (src/Shared/Osd.cs reads the same names)
const MODS = { ctrl: 'Ctrl', control: 'Ctrl', alt: 'Alt', shift: 'Shift', win: 'Win', windows: 'Win' };
const NAMED = ['Space', 'PageUp', 'PageDown', 'End', 'Home', 'Left', 'Up', 'Right', 'Down', 'Insert', 'Delete', 'Pause', 'ScrollLock'];

/** "ctrl+alt+f9" → "Ctrl+Alt+F9", the way the PC writes it (one set through the API may be written any way). */
export function tidyHotkey(text) {
  return text.trim().split(/\+(?!$)/).map((part) => {
    const p = part.trim();
    const low = p.toLowerCase();
    if (MODS[low]) return MODS[low];
    if (/^num /i.test(p)) return `Num ${p.slice(4).trim().toUpperCase()}`;
    return NAMED.find((n) => n.toLowerCase() === low) || p.toUpperCase();
  }).join('+');
}

const CODE_KEYS = {
  Space: 'Space', PageUp: 'PageUp', PageDown: 'PageDown', End: 'End', Home: 'Home', Insert: 'Insert', Delete: 'Delete',
  ArrowLeft: 'Left', ArrowUp: 'Up', ArrowRight: 'Right', ArrowDown: 'Down', Pause: 'Pause', ScrollLock: 'ScrollLock',
  NumpadMultiply: 'Num *', NumpadAdd: 'Num +', NumpadSubtract: 'Num -', NumpadDecimal: 'Num .', NumpadDivide: 'Num /',
  Semicolon: ';', Equal: '=', Comma: ',', Minus: '-', Period: '.', Slash: '/', Backquote: '`', BracketLeft: '[',
  Backslash: '\\', BracketRight: ']', Quote: "'",
};

/** The key a keyboard event names, in the shortcut's words; null for a modifier alone or a key that can't be used. */
export function keyName(e) {
  const c = e.code || '';
  if (/^Key[A-Z]$/.test(c)) return c.slice(3);
  if (/^Digit[0-9]$/.test(c)) return c.slice(5);
  if (/^F([1-9]|1[0-9]|2[0-4])$/.test(c)) return c;
  if (/^Numpad[0-9]$/.test(c)) return `Num ${c.slice(6)}`;
  return CODE_KEYS[c] || null;
}

/**
 * A shortcut from a key press ("Ctrl+Shift+S"), or { error } when it can't be one: a letter, digit or sign needs
 * Ctrl, Alt or Win, or it would be taken from typing everywhere.
 */
export function hotkeyFromEvent(e) {
  const key = keyName(e);
  if (!key) return null;
  const mods = [e.ctrlKey && 'Ctrl', e.altKey && 'Alt', e.shiftKey && 'Shift', e.metaKey && 'Win'].filter(Boolean);
  const alone = /^(F\d+|Num .+|Pause|ScrollLock|Insert|Home|End|PageUp|PageDown)$/.test(key);
  if (!alone && !(e.ctrlKey || e.altKey || e.metaKey)) return { error: `${[...mods, key].join('+')} would get in the way of typing: add Ctrl or Alt.` };
  return { hotkey: [...mods, key].join('+') };
}
