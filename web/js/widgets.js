// Dashboard widgets: gauges (five styles), horizontal and vertical bars, graph, value. Plain DOM/SVG/canvas, no libraries.
const SVG = 'http://www.w3.org/2000/svg';
/** Points kept for live graphs (one per update). */
export const HISTORY = 120;

export const COLORS = { min: '#22c55e', med: '#f59e0b', max: '#ef4444' };

/** Memory and storage sizes can be shown in GB or MB. */
export function unitChoices(sensor) {
  return sensor && (sensor.unit === 'GB' || sensor.unit === 'MB') ? ['GB', 'MB'] : null;
}

/** Multiplier from one size unit to another (GB ↔ MB); 1 for everything else. */
export function unitFactor(from, to) {
  if (from === 'GB' && to === 'MB') return 1024;
  if (from === 'MB' && to === 'GB') return 1 / 1024;
  return 1;
}

/** The unit a widget shows: its chosen size unit, else the sensor's own. */
export function displayUnit(w, sensor) {
  const native = sensor?.unit || '';
  return unitChoices(sensor) && w.unit ? w.unit : native;
}

/**
 * Sensors whose live sum is the natural scale maximum for a "used"/"free" reading on the same hardware:
 * "GPU Memory Used" → ["GPU Memory Total"], "Memory Used" → ["Memory Used", "Memory Available"].
 */
export function totalFor(sensor, catalog = []) {
  if (!sensor || !['Data', 'SmallData'].includes(sensor.type)) return null;
  const siblings = catalog.filter((s) => s.hardware === sensor.hardware && s.unit === sensor.unit && s.id !== sensor.id);
  const named = (n) => siblings.find((s) => s.name.toLowerCase() === n.toLowerCase());
  const swap = (from, to) => sensor.name.replace(new RegExp(from, 'i'), to);
  if (/used/i.test(sensor.name)) {
    const total = named(swap('used', 'Total'));
    if (total) return [total.id];
    const free = named(swap('used', 'Free')) || named(swap('used', 'Available'));
    if (free) return [sensor.id, free.id];
  }
  for (const word of ['free', 'available']) {
    if (!new RegExp(word, 'i').test(sensor.name)) continue;
    const total = named(swap(word, 'Total'));
    if (total) return [total.id];
    const used = named(swap(word, 'Used'));
    if (used) return [sensor.id, used.id];
  }
  // A reading from another family on the same device, e.g. Windows' "D3D Dedicated Memory Used" next to the
  // driver's "GPU Memory Total": use the device's own memory total. Shared/virtual memory has no such total.
  if (/used|free|available/i.test(sensor.name) && !/shared|virtual/i.test(sensor.name)) {
    const total = siblings.find((s) => /total/i.test(s.name) && /memory/i.test(s.name) && !/shared|virtual/i.test(s.name));
    if (total) return [total.id];
  }
  return null;
}

/** Live scale maximum sources for a widget: its own choice, or (for widgets made before this existed) the detected total. */
export function effectiveMaxFrom(w, sensor, catalog) {
  if (w.maxFrom !== undefined) return w.maxFrom?.length ? w.maxFrom : null;
  return w.max == null ? totalFor(sensor, catalog) : null;
}

/** Sensible defaults when a sensor is picked for a new widget. */
export function defaultsFor(sensor, catalog = []) {
  const t = sensor.type;
  const pct = ['Load', 'Control', 'Level', 'Humidity'].includes(t) || sensor.unit === '%';
  const w = {
    sensor: sensor.id,
    label: sensor.name,
    type: t === 'Throughput' ? 'graph' : (pct || t === 'Temperature') ? 'gauge' : (t === 'Data' || t === 'SmallData') ? 'bar' : 'value',
    style: 'arc',
    min: 0,
    max: pct || t === 'Temperature' ? 100 : null,
    decimals: ['Voltage', 'Current'].includes(t) ? 2 : (t === 'Data' || t === 'Frametime' ? 1 : 0),
    maxFrom: totalFor(sensor, catalog),
    thresholds: [
      { value: null, color: COLORS.min },
      { value: null, color: COLORS.med },
      { value: null, color: COLORS.max },
    ],
  };
  if (t === 'Temperature') setT(w, 0, 70, 85);
  else if (t === 'Load' && !/space/i.test(sensor.name)) setT(w, 0, 70, 90);
  else if (/used space/i.test(sensor.name)) setT(w, 0, 80, 95);
  else if (t === 'Fps') {
    w.type = 'value';
    w.thresholds = [
      { value: 0, color: COLORS.max },
      { value: 30, color: COLORS.med },
      { value: 60, color: COLORS.min },
    ];
  } else if (t === 'Frametime') {
    w.type = 'graph';
    setT(w, 0, 20, 33);   // above 20 ms is under 50 FPS, above 33 ms under 30 FPS
  }
  // A fan's speed in % and in RPM belong together: LibreHardwareMonitor names both after the fan.
  const partner = { Control: 'Fan', Fan: 'Control' }[t];
  const twin = partner && catalog.find((s) => s.type === partner && s.hardware === sensor.hardware && s.name === sensor.name);
  if (twin) w.extra = [{ sensor: twin.id, show: 'value' }];
  [w.w, w.h] = defaultSize(w.type);
  return w;
}

/** How many more readings a card can show under its main one (3 sensors per card in all). */
export const MAX_EXTRA = 2;

/** How an extra reading is shown: [value, label]. */
export const EXTRA_SHOW = [['value', 'Value'], ['both', 'Value and graph'], ['graph', 'Graph only']];
/** Longest label an extra reading can have. */
export const MAX_EXTRA_LABEL = 40;

/** The text shown before an extra reading's value: its own label, or the sensor's name when it shares the main unit. */
export function extraLabel(x, s, main) {
  if (typeof x.label === 'string') return x.label;
  return s && main && s.unit === main.unit ? s.name : '';
}

/** Where an extra reading's value sits on its line: [value, label]. */
export const EXTRA_ALIGN = [['left', 'Left'], ['center', 'Center'], ['right', 'Right']];
/** Text size of an extra reading: [value, label, scale]. It still grows and shrinks with the card. */
export const EXTRA_SIZE = [['s', 'Small', 0.8], ['m', 'Normal', 1], ['l', 'Large', 1.35], ['xl', 'Extra large', 1.75]];

/**
 * A widget's extra readings as [{ sensor, show, align, size, label? }] (older layouts stored bare sensor ids).
 * `label` is the text before the value; without one, the sensor's name is shown only when its unit is the main
 * reading's (so native and shown FPS can be told apart). An empty label shows none.
 */
export function extrasOf(w) {
  if (!Array.isArray(w?.extra)) return [];
  const seen = new Set([w.sensor]);
  const out = [];
  for (const x of w.extra) {
    const e = typeof x === 'string' ? { sensor: x } : x;
    if (!e || typeof e.sensor !== 'string' || seen.has(e.sensor)) continue;
    seen.add(e.sensor);
    out.push({
      sensor: e.sensor,
      show: EXTRA_SHOW.some(([k]) => k === e.show) ? e.show : 'value',
      align: EXTRA_ALIGN.some(([k]) => k === e.align) ? e.align : 'center',
      size: EXTRA_SIZE.some(([k]) => k === e.size) ? e.size : 'm',
      ...(typeof e.label === 'string' ? { label: e.label.trim().slice(0, MAX_EXTRA_LABEL) } : {}),
    });
  }
  return out.slice(0, MAX_EXTRA);
}

function setText(node, text) {
  if (node.textContent !== text) node.textContent = text;
}

/** A reading as short text with its unit ("1450 RPM", "62 °C", "1.250 V"), for lists and extra readings. */
export function readingText(sensor, v) {
  const unit = sensor?.unit || '';
  if (v == null || isNaN(v)) return '–';
  const whole = ['%', '°C', 'RPM', 'MHz', 'Hz', 'FPS', 'MB', 'dBA', 'mWh', 'L/h'].includes(unit);
  const f = format(v, unit, unit === 'V' ? 3 : whole || Math.abs(v) >= 100 ? 0 : 1);
  return `${f.n}${f.u ? ' ' + f.u : ''}`;
}

/** A widget or section from storage, brought up to date (old "size" → columns × rows). */
export function normalizeItem(x) {
  if (!x || typeof x !== 'object') return null;
  if (x.kind === 'section') return { id: x.id, kind: 'section', title: String(x.title ?? 'Section') };
  const w = { ...x };
  if (!positionsFor(w.type) && w.type !== 'graph' && w.type !== 'value') w.type = 'value';
  if (!(w.w >= 1 && w.h >= 1)) [w.w, w.h] = defaultSize(w.type, w.size);
  w.w = Math.min(MAX_W, Math.round(w.w));
  w.h = Math.min(MAX_H, Math.round(w.h));
  delete w.size;
  const extra = extrasOf(w);
  if (extra.length) w.extra = extra;
  else delete w.extra;
  return w;
}

export const isSection = (x) => x?.kind === 'section';

function setT(w, a, b, c) {
  w.thresholds[0].value = a;
  w.thresholds[1].value = b;
  w.thresholds[2].value = c;
}

/** Colour of the highest threshold the value has reached; null = accent colour. */
export function colorFor(w, v) {
  if (v == null) return null;
  let color = null;
  const ts = (w.thresholds || []).filter((t) => t.value !== null && t.value !== '' && !isNaN(t.value))
    .sort((a, b) => a.value - b.value);
  for (const t of ts) if (v >= Number(t.value)) color = t.color;
  return color;
}

/** Human formatting, including auto-scaling throughput. */
export function format(v, unit, decimals = 0) {
  if (v == null || isNaN(v)) return { n: '–', u: unit };
  if (unit === 'B/s') {
    const steps = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
    let i = 0;
    while (Math.abs(v) >= 1024 && i < steps.length - 1) { v /= 1024; i++; }
    return { n: v.toFixed(i === 0 ? 0 : 1), u: steps[i] };
  }
  return { n: Number(v).toFixed(decimals), u: unit };
}

function niceCeil(x) {
  if (x <= 0) return 1;
  const p = 10 ** Math.floor(Math.log10(x));
  for (const m of [1, 2, 2.5, 5, 10]) if (m * p >= x) return m * p;
  return 10 * p;
}

function el(tag, cls, html) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html != null) e.innerHTML = html;
  return e;
}

function svgEl(tag, attrs = {}) {
  const e = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  return e;
}

function readout(cls = 'readout') {
  const r = el('div', cls + ' num');
  const n = el('span');
  const u = el('span', 'unit');
  r.append(n, u);
  return { root: r, set(f) { n.textContent = f.n; u.textContent = f.u; } };
}

// ---- card sizes: a grid of columns (4 on a phone, more on wider screens) and fixed-height rows

export const MAX_W = 16;
export const MAX_H = 12;
const SIZES = { gauge: [2, 3], bar: [2, 2], vbar: [1, 3], graph: [4, 3], value: [2, 2] };
const WIDE = { gauge: [4, 3], bar: [4, 2], vbar: [2, 3], graph: [4, 3], value: [4, 2] };

/** Default card size [columns, rows] for a display type ("lg" = the old "Wide" size). */
export function defaultSize(type, legacy) {
  return [...((legacy === 'lg' ? WIDE : SIZES)[type] || SIZES.value)];
}

/** A widget's thickness setting as a factor (1 = proportional to the card, the default). */
export const THICKNESS = { min: 25, max: 300, step: 5 };
export function thicknessOf(w) {
  const t = Number(w.thick);
  return t >= THICKNESS.min && t <= THICKNESS.max ? t / 100 : 1;
}

// ---- device icons, shown in the card's top-right corner

const ICONS = {
  cpu: '<rect x="6" y="6" width="12" height="12" rx="2"/><rect x="9.5" y="9.5" width="5" height="5" rx=".5"/><path d="M9.5 2.5v3M14.5 2.5v3M9.5 18.5v3M14.5 18.5v3M2.5 9.5h3M2.5 14.5h3M18.5 9.5h3M18.5 14.5h3"/>',
  gpu: '<path d="M2.5 5.5h19v12h-19z"/><circle cx="9" cy="11.5" r="3.5"/><circle cx="9" cy="11.5" r=".5"/><path d="M15.5 9h3M15.5 12h3M15.5 15h3M5 17.5v2.5M9 17.5v2.5"/>',
  mem: '<path d="M2.5 6.5h19v9h-19z"/><path d="M6 9.5v3M10 9.5v3M14 9.5v3M18 9.5v3M5 15.5v3M8.5 15.5v3M12 15.5v3M15.5 15.5v3M19 15.5v3"/>',
  disk: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 14h18M7 17h5"/><circle cx="17" cy="17" r=".6"/>',
  net: '<path d="M7.5 3.5v15M3.5 14.5l4 4 4-4M16.5 20.5v-15M12.5 9.5l4-4 4 4"/>',
  board: '<rect x="3" y="3" width="18" height="18" rx="2"/><rect x="7" y="7" width="6" height="6" rx="1"/><path d="M16.5 7v3M16.5 13v1M7 17h10M3 10h1.5M3 14h1.5"/>',
  fan: '<circle cx="12" cy="12" r="1.8"/><path d="M12 10.2C11 6.5 12 3 14.6 3.4c2.4.4 2.4 3.6.3 5.6L13.6 10.4M13.8 12c3.7-1 7.2 0 6.8 2.6-.4 2.4-3.6 2.4-5.6.3L13.6 13.6M12 13.8c1 3.7 0 7.2-2.6 6.8-2.4-.4-2.4-3.6-.3-5.6l1.3-1.4M10.2 12c-3.7 1-7.2 0-6.8-2.6.4-2.4 3.6-2.4 5.6-.3l1.4 1.3"/>',
  psu: '<path d="M9 2.5v5M15 2.5v5M6 7.5h12v3.5a6 6 0 0 1-12 0Z"/><path d="M12 17v4.5"/>',
  battery: '<rect x="2.5" y="7" width="16.5" height="10" rx="2"/><path d="M21.5 10.5v3M6 10v4M9.5 10v4"/>',
  game: '<path d="M7.5 11h4M9.5 9v4M15.5 12h.01M18 10.5h.01"/><path d="M17 5.5H7a4 4 0 0 0-3.9 3.4l-1 6.4a2.8 2.8 0 0 0 4.8 2.4L8.4 16h7.2l1.5 1.7a2.8 2.8 0 0 0 4.8-2.4l-1-6.4A4 4 0 0 0 17 5.5Z"/>',
  other: '<path d="M3 12h4l3-7 4 14 3-7h4"/>',
};

/** Which icon a sensor's device gets. */
export function deviceKind(sensor) {
  const t = sensor?.hardwareType || '';
  if (t === 'Cpu') return 'cpu';
  if (t.startsWith('Gpu')) return 'gpu';
  if (t === 'Memory') return 'mem';
  if (t === 'Storage' || t === 'Drive') return 'disk';
  if (t === 'Network') return 'net';
  if (t === 'Cooler') return 'fan';
  if (t === 'Psu') return 'psu';
  if (t === 'Battery') return 'battery';
  if (t === 'Game') return 'game';
  if (['Motherboard', 'SuperIO', 'EmbeddedController'].includes(t)) return 'board';
  return 'other';
}

export function deviceIcon(sensor) {
  return `<svg viewBox="0 0 24 24" aria-hidden="true">${ICONS[deviceKind(sensor)]}</svg>`;
}

// ---- gauge styles and where the value sits

export const GAUGE_STYLES = [['arc', 'Arc'], ['semi', 'Semicircle'], ['ring', 'Ring'], ['needle', 'Needle'], ['segments', 'Segments']];
const GAUGE = {
  //        viewBox   centre    radius  from → to (degrees, 0 = 3 o'clock, clockwise)  value y (% of height)              font (% of width)
  arc: { vb: [120, 100], c: [60, 60], r: 48, a: [150, 390], y: { top: 36, center: 58, bottom: 86 }, pos: 'center', fs: 18 },
  semi: { vb: [120, 68], c: [60, 62], r: 52, a: [180, 360], y: { top: 48, center: 68, bottom: 88 }, pos: 'bottom', fs: 17 },
  ring: { vb: [120, 120], c: [60, 60], r: 50, a: [-90, 270], y: { top: 33, center: 50, bottom: 67 }, pos: 'center', fs: 19 },
  needle: { vb: [120, 100], c: [60, 62], r: 48, a: [150, 390], y: { top: 34, center: 58, bottom: 88 }, pos: 'bottom', fs: 15 },
  segments: { vb: [120, 100], c: [60, 60], r: 48, a: [150, 390], y: { top: 36, center: 58, bottom: 86 }, pos: 'center', fs: 18 },
};

/** Value position choices per display type: [value, label]. The first is the default where a style has none. */
export function positionsFor(type) {
  if (type === 'gauge') return [['top', 'Top'], ['center', 'Center'], ['bottom', 'Bottom']];
  if (type === 'bar') return [['top', 'Above, left'], ['top-right', 'Above, right'], ['bottom', 'Below, left'], ['bottom-right', 'Below, right'], ['left', 'Left of the bar'], ['right', 'Right of the bar'], ['inside', 'Inside the bar']];
  if (type === 'vbar') return [['top', 'Above'], ['bottom', 'Below'], ['left', 'Left of the bar'], ['right', 'Right of the bar'], ['inside', 'Inside the bar']];
  if (type === 'graph') return [['top', 'Above the graph'], ['none', 'Hidden (graph only)']];
  return null;
}

export function defaultPosition(type, style) {
  if (type === 'gauge') return (GAUGE[style] || GAUGE.arc).pos;
  return positionsFor(type)?.[0][0] || null;
}

const rad = (a) => a * Math.PI / 180;
const pt = (c, r, a) => [c[0] + r * Math.cos(rad(a)), c[1] + r * Math.sin(rad(a))];
function arcD(c, r, a0, a1) {
  if (a1 - a0 >= 360) a1 = a0 + 359.99;
  const [x0, y0] = pt(c, r, a0);
  const [x1, y1] = pt(c, r, a1);
  return `M${x0.toFixed(2)} ${y0.toFixed(2)} A${r} ${r} 0 ${a1 - a0 > 180 ? 1 : 0} 1 ${x1.toFixed(2)} ${y1.toFixed(2)}`;
}

/**
 * Creates a widget card. Returns { el, update(value, visible, max, extras), setSensor(sensor, lookup), refresh(), destroy() }.
 * `sensor` may be undefined while the catalog is still loading or the hardware is gone. `max` is the live scale
 * maximum (e.g. total VRAM) in the sensor's own unit, when the widget has one. `extras` are the values of the
 * widget's extra readings (w.extra), shown as a line under the main display; `lookup(id)` gives their sensor info.
 * `series(id)`, when given, returns a sensor's recent readings (raw, oldest first), kept by the dashboard so a card
 * that is rebuilt (after an edit, or switching between the desktop and phone layouts) keeps its graph; without it
 * the card keeps its own. The card's size (grid span) is set by the dashboard; everything inside scales with the card.
 */
export function createWidget(w, sensor, tools, lookup = () => undefined, series = null) {
  const shared = !!series;
  const seriesOf = series || (() => []);
  const highest = (a) => a.reduce((m, v) => (v != null && v > m ? v : m), 0);
  const card = el('div', 'card');
  card.dataset.id = w.id;
  card.dataset.type = w.type;
  const head = el('div', 'card-head');
  const label = el('span', 'label');
  label.textContent = w.label || sensor?.name || 'Sensor';
  const hw = el('span', 'hw');
  hw.textContent = w.sub || (sensor ? sensor.hardware : 'unavailable');
  hw.hidden = !!w.hideSub;
  card.classList.toggle('no-sub', !!w.hideSub);
  head.append(label, hw);
  const icon = el('span', 'card-icon', deviceIcon(sensor));
  const bodyWrap = el('div', 'card-body');
  card.append(head, icon, bodyWrap);

  const unit = displayUnit(w, sensor);
  const k = unitFactor(sensor?.unit, unit);
  const scaleMin = Number(w.min) || 0;
  let peak = highest(seriesOf(w.sensor)) * k;
  let liveMax = null;
  const scaleMax = () => (w.max !== null && w.max !== '' && w.max !== undefined && !isNaN(w.max) ? Number(w.max)
    : liveMax > scaleMin ? liveMax : niceCeil(peak * 1.1));
  const frac = (v) => Math.max(0, Math.min(1, (v - scaleMin) / ((scaleMax() - scaleMin) || 1)));
  const thresholds = (w.thresholds || []).filter((t) => t.value !== null && t.value !== '' && !isNaN(t.value))
    .sort((a, b) => a.value - b.value);
  const pos = positionsFor(w.type)?.some(([p]) => p === w.pos) ? w.pos : defaultPosition(w.type, w.style);
  // Bar / arc / line thickness: proportional to the card by default; the widget's setting scales that (25–300 %).
  const tk = thicknessOf(w);

  let impl;
  if (w.type === 'gauge') impl = gauge();
  else if (w.type === 'bar') impl = hbar();
  else if (w.type === 'vbar') impl = vbar();
  else if (w.type === 'graph') impl = graph();
  else impl = value();

  bodyWrap.append(impl.body);

  // Extra readings under the main display, e.g. a fan's RPM under its % gauge or CPU power under CPU load.
  const extras = extrasOf(w).map((x) => {
    const hist = seriesOf(x.sensor);
    return { ...x, s: lookup(x.sensor), v: hist.length ? hist[hist.length - 1] : null, hist, peak: highest(hist) };
  });
  let extraRo = null;
  if (extras.length) card.append(extraRows());
  if (tools) card.append(tools);

  let last = null;
  return {
    el: card,
    update(v, visible, max, xs) {
      if (!shared) impl.push?.(v);
      if (v != null) v *= k;
      if (max != null) liveMax = max * k;
      last = v;
      if (v != null && v > peak) peak = v;
      if (xs) extras.forEach((x, i) => {
        x.v = xs[i] ?? null;
        if (x.v != null && x.v > x.peak) x.peak = x.v;
        if (!shared && x.show !== 'value') { x.hist.push(x.v); if (x.hist.length > HISTORY) x.hist.shift(); }
      });
      if (visible) paint();
    },
    refresh() { paint(); },
    setSensor(s, find = lookup) {
      if (!w.label) label.textContent = s?.name || 'Sensor';
      hw.textContent = w.sub || (s ? s.hardware : 'unavailable');
      extras.forEach((x) => { x.s = find(x.sensor); });
    },
    destroy() { impl.destroy?.(); extraRo?.disconnect(); },
  };

  function paint() {
    const c = colorFor(w, last);
    card.style.setProperty('--c', c || 'var(--accent)');
    impl.paint(last, c);
    for (const x of extras) paintExtra(x);
  }

  // ---- extra readings: consecutive value-only ones share a line, each in its left / center / right slot; the
  // others get a row with a small graph, the value on the chosen side (or centered above it).
  function extraRows() {
    const wrap = el('div', 'card-extra');
    let line = null;
    for (const x of extras) {
      const scale = EXTRA_SIZE.find(([k]) => k === x.size)?.[2] ?? 1;
      if (x.show !== 'graph') x.text = readingEl(scale);
      if (x.show === 'value') {
        if (!line) {
          wrap.append(line = el('div', 'x-line'));
          line.append(el('span', 'x-slot x-left'), el('span', 'x-slot x-center'), el('span', 'x-slot x-right'));
        }
        line.querySelector(`.x-${x.align}`).append(x.text);
        x.tipEl = x.text;
        continue;
      }
      line = null;
      const row = el('div', `x-row x-${x.show} x-at-${x.align}`);
      row.style.setProperty('--xs', scale);
      x.canvas = el('canvas');
      if (x.text) row.append(x.text);
      row.append(x.canvas);
      x.tipEl = row;
      wrap.append(row);
    }
    for (const l of wrap.querySelectorAll('.x-line')) l.classList.toggle('has-center', !!l.querySelector('.x-center > *'));
    if (extras.some((x) => x.canvas)) {
      extraRo = new ResizeObserver(() => extras.forEach((x) => { x.sized = false; drawSpark(x); }));
      extras.forEach((x) => x.canvas && extraRo.observe(x.canvas));
    }
    return wrap;
  }

  /** An extra reading's text: optional name, number and unit, styled like the main reading (number bright, unit muted). */
  function readingEl(scale) {
    const t = el('span', 'x-v');
    t.style.setProperty('--xs', scale);
    t.append(el('span', 'x-name'), el('span', 'x-num'), el('span', 'x-unit'));
    return t;
  }

  function paintExtra(x) {
    const name = extraLabel(x, x.s, sensor);
    const reading = x.s ? readingText(x.s, x.v) : '–';
    const text = (name ? `${name} ` : '') + reading;
    if (x.text) {
      const cut = reading.indexOf(' ');
      const [nameEl, numEl, unitEl] = x.text.children;
      setText(nameEl, name);
      setText(numEl, cut < 0 ? reading : reading.slice(0, cut));
      setText(unitEl, cut < 0 ? '' : reading.slice(cut + 1));
    }
    const tip = x.s ? `${x.s.name} (${x.s.hardware}): ${text}` : 'Sensor not available';
    if (x.tipEl.title !== tip) x.tipEl.title = tip;
    drawSpark(x);
  }

  /** A small rolling line chart for an extra reading: from 0 (or the lowest reading below 0) to its peak. */
  function drawSpark(x) {
    const c = x.canvas;
    if (!c) return;
    const dpr = window.devicePixelRatio || 1;
    const wpx = c.clientWidth, hpx = c.clientHeight;
    if (!wpx || !hpx) return;
    if (!x.sized) { c.width = Math.round(wpx * dpr); c.height = Math.round(hpx * dpr); x.sized = true; }
    const ctx = c.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, wpx, hpx);
    const pts = x.hist;
    if (pts.length < 2) return;
    const lo = Math.min(0, ...pts.filter((p) => p != null));
    const hi = x.s?.unit === '%' ? 100 : niceCeil(x.peak * 1.1);
    const lw = Math.min(8, Math.max(1.25, Math.min(3, hpx / 16)) * tk);
    const pad = Math.max(1.5, lw / 2);
    const y = (v) => hpx - pad - ((v - lo) / ((hi - lo) || 1)) * (hpx - 2 * pad);
    const step = wpx / (HISTORY - 1);
    const x0 = wpx - (pts.length - 1) * step;
    const stroke = getComputedStyle(card).getPropertyValue('--muted').trim() || '#94a3b8';
    ctx.beginPath();
    let started = false;
    pts.forEach((v, i) => {
      if (v == null) { started = false; return; }
      const px = x0 + i * step;
      if (!started) { ctx.moveTo(px, y(v)); started = true; } else ctx.lineTo(px, y(v));
    });
    ctx.strokeStyle = stroke;
    ctx.lineWidth = lw;
    ctx.lineJoin = 'round';
    ctx.stroke();
    ctx.lineTo(wpx, hpx); ctx.lineTo(x0, hpx); ctx.closePath();
    ctx.globalAlpha = 0.14;
    ctx.fillStyle = stroke;
    ctx.fill();
    ctx.globalAlpha = 1;
  }

  /** Colour of the threshold zone a value falls in (for zones drawn on the scale). */
  function zoneColor(v) {
    let color = null;
    for (const t of thresholds) if (v >= Number(t.value)) color = t.color;
    return color;
  }

  // ---- gauges
  function gauge() {
    const style = GAUGE[w.style] ? w.style : 'arc';
    const g = GAUGE[style];
    const [a0, a1] = g.a;
    const angle = (f) => a0 + (a1 - a0) * f;
    const body = el('div', 'gauge');
    const box = el('div', 'gbox');
    box.dataset.style = style;
    box.style.setProperty('--ar', String(g.vb[0] / g.vb[1]));
    box.style.setProperty('--fs', String(g.fs));
    box.style.setProperty('--y', `${g.y[pos] ?? g.y.center}%`);
    const svg = svgEl('svg', { viewBox: `0 0 ${g.vb[0]} ${g.vb[1]}` });
    box.append(svg);
    const r = readout();
    box.append(r.root);
    body.append(box);

    const ticks = svgEl('g');
    let draw;   // (v, f) → void

    // The outer edge stays put; a thicker stroke grows inwards.
    const stroke = (base, min, max) => Math.max(min, Math.min(max, base * tk));
    if (style === 'segments') {
      const sw = stroke(12, 2, 28);   // capped so the value still fits inside
      const r0 = g.r + 6 - sw / 2;
      const n = 20;
      const step = (a1 - a0) / n;
      const segs = [];
      for (let i = 0; i < n; i++) {
        const s = svgEl('path', { d: arcD(g.c, r0, a0 + i * step + 1.2, a0 + (i + 1) * step - 1.2), class: 'seg' });
        s.style.strokeWidth = sw;
        svg.append(s);
        segs.push(s);
      }
      draw = (v, f) => {
        const lit = v == null ? 0 : Math.max(f > 0 ? 1 : 0, Math.round(f * n));
        const max = scaleMax();
        segs.forEach((s, i) => {
          const on = i < lit;
          s.classList.toggle('on', on);
          s.style.stroke = on ? (zoneColor(scaleMin + (max - scaleMin) * (i + 0.5) / n) || '') : '';
        });
      };
    } else if (style === 'needle') {
      const sw = stroke(6, 1.5, 22);
      const r0 = g.r + 3 - sw / 2;
      const inner = r0 - sw / 2;
      const track = svgEl('path', { d: arcD(g.c, r0, a0, a1), class: 'track' });
      track.style.strokeWidth = sw;
      svg.append(track);
      const zones = svgEl('g');
      svg.append(zones, ticks);
      const needle = svgEl('line', { x1: g.c[0], y1: g.c[1], x2: g.c[0] + inner - 3, y2: g.c[1], class: 'needle' });
      needle.style.transformOrigin = `${g.c[0]}px ${g.c[1]}px`;
      needle.style.strokeWidth = Math.max(1.5, Math.min(6, 3 * Math.sqrt(tk)));
      svg.append(needle, svgEl('circle', { cx: g.c[0], cy: g.c[1], r: Math.max(3, Math.min(8, 4.5 * Math.sqrt(tk))), class: 'hub' }));
      let zonesMax = null;
      draw = (v, f) => {
        const max = scaleMax();
        if (max !== zonesMax) {
          zonesMax = max;
          zones.replaceChildren();
          ticks.replaceChildren();
          thresholds.forEach((t, i) => {
            const from = frac(Number(t.value));
            const to = i + 1 < thresholds.length ? frac(Number(thresholds[i + 1].value)) : 1;
            if (to > from) {
              const z = svgEl('path', { d: arcD(g.c, r0, angle(from), angle(to)), class: 'zone', stroke: t.color });
              z.style.strokeWidth = sw;
              zones.append(z);
            }
          });
          for (let i = 0; i <= 10; i++) {
            const [x1, y1] = pt(g.c, inner - 1, angle(i / 10));
            const [x2, y2] = pt(g.c, inner - (i % 5 ? 4 : 7), angle(i / 10));
            ticks.append(svgEl('line', { x1, y1, x2, y2, class: 'scale-tick' }));
          }
        }
        needle.style.transform = `rotate(${angle(v == null ? 0 : f)}deg)`;
      };
    } else {
      // arc, semicircle, ring: a track with a filled part and threshold ticks
      const sw = stroke(10, 2, 32);
      const r0 = g.r + 5 - sw / 2;
      const d = arcD(g.c, r0, a0, a1);
      const track = svgEl('path', { d, class: 'track', pathLength: 100 });
      const fill = svgEl('path', { d, class: 'fill', pathLength: 100, 'stroke-dasharray': '0 100' });
      track.style.strokeWidth = fill.style.strokeWidth = sw;
      svg.append(track, fill, ticks);
      let tickMax = null;
      draw = (v, f) => {
        const max = scaleMax();
        if (max !== tickMax) {
          tickMax = max;
          ticks.replaceChildren();
          for (const t of thresholds) {
            if (Number(t.value) <= scaleMin) continue;
            const a = angle(frac(Number(t.value)));
            const [x1, y1] = pt(g.c, r0 - sw / 2 - 3, a);
            const [x2, y2] = pt(g.c, r0 + sw / 2 + 3, a);
            ticks.append(svgEl('line', { x1, y1, x2, y2, class: 'tick', stroke: t.color }));
          }
        }
        fill.setAttribute('stroke-dasharray', `${v == null ? 0 : f * 100} 100`);
        fill.style.opacity = v == null || f <= 0 ? '0' : '1';
      };
    }

    return {
      body,
      paint(v) {
        draw(v, v == null ? 0 : frac(v));
        r.set(format(v, unit, w.decimals));
      },
    };
  }

  // ---- bars: shared track, fill, threshold ticks and min/max labels
  function barParts(vertical) {
    const r = readout();
    const track = el('div', 'bar-track');
    const fill = el('div', 'bar-fill');
    track.append(fill);
    const scale = el('div', 'bar-scale');
    const lo = el('span'); const hi = el('span');
    if (vertical) scale.append(hi, lo); else scale.append(lo, hi);
    let tickMax = null;
    return {
      r, track, scale,
      paint(v) {
        const max = scaleMax();
        if (max !== tickMax) {
          tickMax = max;
          track.querySelectorAll('.bar-tick').forEach((t) => t.remove());
          for (const t of thresholds) {
            if (Number(t.value) <= scaleMin) continue;
            const tick = el('div', 'bar-tick');
            const at = `calc(${frac(Number(t.value)) * 100}% - 1px)`;
            if (vertical) tick.style.bottom = at; else tick.style.left = at;
            tick.style.background = t.color;
            track.append(tick);
          }
          const a = format(scaleMin, unit, 0); const b = format(max, unit, 0);
          lo.textContent = a.n; hi.textContent = `${b.n} ${b.u}`;
        }
        const pct = `${v == null ? 0 : frac(v) * 100}%`;
        if (vertical) fill.style.height = pct; else fill.style.width = pct;
        r.set(format(v, unit, w.decimals));
      },
    };
  }

  function hbar() {
    const body = el('div', `barw pos-${pos}`);
    body.style.setProperty('--tk', tk);
    const p = barParts(false);
    if (pos === 'inside') p.track.append(p.r.root);
    else body.append(p.r.root);
    body.append(p.track, p.scale);
    return { body, paint: p.paint };
  }

  function vbar() {
    const body = el('div', `vbarw pos-${pos}`);
    body.style.setProperty('--tk', tk);
    const p = barParts(true);
    if (pos === 'inside') p.track.append(p.r.root);
    else body.append(p.r.root);
    body.append(p.track, p.scale);
    return { body, paint: p.paint };
  }

  // ---- graph: rolling line chart on canvas
  function graph() {
    const body = el('div', `graph pos-${pos}`);
    const r = readout();
    const canvas = el('canvas');
    body.append(r.root, canvas);
    const ctx = canvas.getContext('2d');
    const hist = seriesOf(w.sensor);   // raw readings; drawn in the card's unit
    let dirtySize = true;
    const ro = new ResizeObserver(() => { dirtySize = true; draw(); });
    ro.observe(canvas);
    let color = null;

    function draw() {
      const dpr = window.devicePixelRatio || 1;
      const wpx = canvas.clientWidth, hpx = canvas.clientHeight;
      if (!wpx || !hpx) return;
      if (dirtySize) {
        canvas.width = Math.round(wpx * dpr);
        canvas.height = Math.round(hpx * dpr);
        dirtySize = false;
      }
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, wpx, hpx);
      const styles = getComputedStyle(card);
      const stroke = color || styles.getPropertyValue('--accent').trim() || '#38bdf8';
      const max = scaleMax();
      // Line thickness grows with the card, times the widget's setting; the plot keeps half of it clear at the edges.
      const lw = Math.min(12, Math.max(1.5, Math.min(4, hpx / 45)) * tk);
      const pad = Math.max(2, lw / 2);
      const y = (v) => hpx - pad - frac(v) * (hpx - 2 * pad);
      const step = wpx / (HISTORY - 1);

      // threshold guide lines
      ctx.lineWidth = 1;
      ctx.setLineDash([3, 4]);
      for (const t of thresholds) {
        const tv = Number(t.value);
        if (tv <= scaleMin || tv >= max) continue;
        ctx.strokeStyle = t.color;
        ctx.globalAlpha = 0.6;
        ctx.beginPath(); ctx.moveTo(0, y(tv)); ctx.lineTo(wpx, y(tv)); ctx.stroke();
      }
      ctx.setLineDash([]);
      ctx.globalAlpha = 1;

      if (hist.length < 2) return;
      const x0 = wpx - (hist.length - 1) * step;
      ctx.beginPath();
      let started = false;
      hist.forEach((v, i) => {
        if (v == null) { started = false; return; }
        const x = x0 + i * step;
        if (!started) { ctx.moveTo(x, y(v * k)); started = true; } else ctx.lineTo(x, y(v * k));
      });
      ctx.strokeStyle = stroke;
      ctx.lineWidth = lw;
      ctx.lineJoin = 'round';
      ctx.stroke();
      // soft fill under the line
      ctx.lineTo(wpx, hpx); ctx.lineTo(x0, hpx); ctx.closePath();
      ctx.globalAlpha = 0.12;
      ctx.fillStyle = stroke;
      ctx.fill();
      ctx.globalAlpha = 1;
    }

    return {
      body,
      push(v) { hist.push(v); if (hist.length > HISTORY) hist.shift(); },
      paint(v, c) { color = c; r.set(format(v, unit, w.decimals)); draw(); },
      destroy() { ro.disconnect(); },
    };
  }

  // ---- value: big number
  function value() {
    const body = el('div', 'valuew');
    const r = readout();
    body.append(r.root);
    return { body, paint(v) { r.set(format(v, unit, w.decimals)); } };
  }
}

/** Picks a starter dashboard, in sections, from whatever sensors the server has. Sized for a phone (4 columns). */
export function suggest(catalog) {
  const out = [];
  let section = null;
  const begin = (title) => { section = { kind: 'section', title, used: false }; };
  const add = (w) => {
    if (section && !section.used) { out.push({ kind: 'section', title: section.title }); section.used = true; }
    out.push(w);
  };
  const pick = (pred, overrides = {}) => {
    const s = catalog.find(pred);
    if (!s) return null;
    const w = { ...defaultsFor(s, catalog), ...overrides };
    if (!overrides.w) [w.w, w.h] = defaultSize(w.type);
    if (overrides.w && !overrides.h) w.h = defaultSize(w.type)[1];
    if (!out.some((x) => x.sensor === s.id && x.type === w.type)) add(w);
    return s;
  };
  const isCpu = (s) => s.hardwareType === 'Cpu';
  const isGpu = (s) => s.hardwareType.startsWith('Gpu');
  const named = (re) => (s) => re.test(s.name);
  // An extra reading for a card, when the PC has it: e.g. power as a number with a small graph under the load gauge.
  const extra = (pred, show) => {
    const s = catalog.find(pred);
    return s ? { extra: [{ sensor: s.id, show }] } : {};
  };
  const cpuPower = extra((s) => isCpu(s) && s.type === 'Power' && named(/package|cpu power|ppt/i)(s), 'both');
  // GPU power in watts; without it, NVIDIA's power as a share of the card's power limit.
  const gpuWatts = extra((s) => isGpu(s) && s.type === 'Power' && named(/package|power|board|total/i)(s), 'both');
  const gpuPower = gpuWatts.extra ? gpuWatts : extra((s) => isGpu(s) && s.type === 'Load' && named(/^gpu power$/i)(s), 'both');

  begin('CPU');
  pick((s) => isCpu(s) && s.type === 'Load' && named(/total/i)(s), { label: 'CPU load', ...cpuPower, ...(cpuPower.extra ? { w: 2, h: 4 } : {}) });
  pick((s) => isCpu(s) && s.type === 'Temperature' && named(/package|tctl|tdie|core max/i)(s), { label: 'CPU temp', style: 'needle' })
    || pick((s) => isCpu(s) && s.type === 'Temperature', { label: 'CPU temp', style: 'needle' });
  pick((s) => isCpu(s) && s.type === 'Load' && named(/total/i)(s), { label: 'CPU history', type: 'graph', w: 4, h: 3 });

  begin('GPU');
  pick((s) => isGpu(s) && s.type === 'Load' && named(/core/i)(s), { label: 'GPU load', style: 'segments', ...gpuPower, ...(gpuPower.extra ? { w: 2, h: 4 } : {}) });
  pick((s) => isGpu(s) && s.type === 'Temperature' && named(/core/i)(s), { label: 'GPU temp', style: 'needle' });
  // The driver's own figure first; Windows' D3D counters as a fallback; never shared (system) memory.
  // The VRAM bar also shows how full it is in %, worked out by the server from used and total.
  const vramPct = (s) => (catalog.some((p) => p.id === `${s.id}/percent`) ? { extra: [{ sensor: `${s.id}/percent`, show: 'value' }] } : {});
  const vramPick = (pred) => {
    const s = catalog.find((x) => (x.type === 'Data' || x.type === 'SmallData') && pred(x));
    return s ? pick((x) => x.id === s.id, { label: 'VRAM used', type: 'bar', w: 4, ...vramPct(s) }) : null;
  };
  vramPick((s) => isGpu(s) && /^gpu memory used$/i.test(s.name))
    || vramPick((s) => isGpu(s) && /dedicated memory used/i.test(s.name))
    || vramPick((s) => isGpu(s) && named(/memory used/i)(s) && !/shared/i.test(s.name));

  begin('Gaming');
  // Our own frame counter works on any GPU (shown frames, plus the game's native ones under frame generation);
  // AMD's driver FPS (via LibreHardwareMonitor) is the fallback.
  pick((s) => s.id === '/frames/fps', { label: 'FPS', ...extra((s) => s.id === '/frames/fps-native', 'value') })
    || pick((s) => s.type === 'Fps', { label: 'FPS' });
  pick((s) => s.id === '/frames/frametime-worst', { label: 'Worst frame', type: 'value' });
  pick((s) => s.id === '/frames/frametime', { label: 'Frame time', w: 4, h: 3 });

  begin('Memory & drives');
  pick((s) => s.hardwareType === 'Memory' && s.type === 'Load', { label: 'RAM', type: 'bar', w: 4 });
  catalog.filter((s) => s.hardwareType === 'Drive' && s.id.endsWith('/used')).slice(0, 4)
    .forEach((s) => add({ ...defaultsFor(s, catalog), label: s.hardware, type: 'vbar', w: 1, h: 3 }));

  begin('Network');
  pick((s) => s.hardwareType === 'Network' && named(/download/i)(s), { label: 'Download', w: 4, h: 3 });
  pick((s) => s.hardwareType === 'Network' && named(/upload/i)(s), { label: 'Upload', w: 4, h: 2 });
  return out;
}
