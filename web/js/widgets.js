// Dashboard widgets: gauge, bar, graph, value. Plain DOM/SVG/canvas, no libraries.
const SVG = 'http://www.w3.org/2000/svg';
const HISTORY = 120;

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
    size: 'sm',
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
  return w;
}

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

function readout(cls = 'readout') {
  const r = el('div', cls + ' num');
  const n = el('span');
  const u = el('span', 'unit');
  r.append(n, u);
  return { root: r, set(f) { n.textContent = f.n; u.textContent = f.u; } };
}

/**
 * Creates a widget card. Returns { el, update(value, visible, max), setSensor(sensor), refresh(), destroy() }.
 * `sensor` may be undefined while the catalog is still loading or the hardware is gone. `max` is the live scale
 * maximum (e.g. total VRAM) in the sensor's own unit, when the widget has one.
 */
export function createWidget(w, sensor, tools) {
  const card = el('div', 'card' + (w.size === 'lg' ? ' lg' : ''));
  card.dataset.id = w.id;
  const head = el('div', 'card-head');
  const label = el('span', 'label');
  label.textContent = w.label || sensor?.name || 'Sensor';
  const hw = el('span', 'hw');
  hw.textContent = sensor ? sensor.hardware : 'unavailable';
  head.append(label, hw);
  card.append(head);

  const unit = displayUnit(w, sensor);
  const k = unitFactor(sensor?.unit, unit);
  const scaleMin = Number(w.min) || 0;
  let peak = 0;
  let liveMax = null;
  const scaleMax = () => (w.max !== null && w.max !== '' && w.max !== undefined && !isNaN(w.max) ? Number(w.max)
    : liveMax > scaleMin ? liveMax : niceCeil(peak * 1.1));
  const frac = (v) => Math.max(0, Math.min(1, (v - scaleMin) / ((scaleMax() - scaleMin) || 1)));
  const thresholds = (w.thresholds || []).filter((t) => t.value !== null && t.value !== '' && !isNaN(t.value));

  let impl;
  if (w.type === 'gauge') impl = gauge();
  else if (w.type === 'bar') impl = bar();
  else if (w.type === 'graph') impl = graph();
  else impl = value();

  card.append(impl.body);
  if (tools) card.append(tools);

  let last = null;
  return {
    el: card,
    update(v, visible, max) {
      if (v != null) v *= k;
      if (max != null) liveMax = max * k;
      last = v;
      if (v != null && v > peak) peak = v;
      impl.push?.(v);
      if (visible) paint();
    },
    refresh() { paint(); },
    setSensor(s) {
      if (!w.label) label.textContent = s?.name || 'Sensor';
      hw.textContent = s ? s.hardware : 'unavailable';
    },
    destroy() { impl.destroy?.(); },
  };

  function paint() {
    const c = colorFor(w, last);
    card.style.setProperty('--c', c || 'var(--accent)');
    impl.paint(last, c);
  }

  // ---- gauge: 240° arc
  function gauge() {
    const body = el('div', 'gauge');
    const svg = document.createElementNS(SVG, 'svg');
    svg.setAttribute('viewBox', '0 0 120 100');
    const d = 'M18.43 84 A48 48 0 1 1 101.57 84';
    const track = document.createElementNS(SVG, 'path');
    track.setAttribute('d', d); track.setAttribute('class', 'track'); track.setAttribute('pathLength', '100');
    const fill = document.createElementNS(SVG, 'path');
    fill.setAttribute('d', d); fill.setAttribute('class', 'fill'); fill.setAttribute('pathLength', '100');
    fill.setAttribute('stroke-dasharray', '0 100');
    svg.append(track, fill);
    const ticks = document.createElementNS(SVG, 'g');
    svg.append(ticks);
    body.append(svg);
    const r = readout();
    body.append(r.root);

    function drawTicks() {
      ticks.replaceChildren();
      for (const t of thresholds) {
        if (Number(t.value) <= scaleMin) continue;
        const a = (150 + 240 * frac(Number(t.value))) * Math.PI / 180;
        const line = document.createElementNS(SVG, 'line');
        line.setAttribute('x1', 60 + 40 * Math.cos(a)); line.setAttribute('y1', 60 + 40 * Math.sin(a));
        line.setAttribute('x2', 60 + 56 * Math.cos(a)); line.setAttribute('y2', 60 + 56 * Math.sin(a));
        line.setAttribute('class', 'tick'); line.setAttribute('stroke', t.color);
        ticks.append(line);
      }
    }
    let tickMax = null;
    return {
      body,
      paint(v) {
        const max = scaleMax();
        if (max !== tickMax) { tickMax = max; drawTicks(); }
        fill.setAttribute('stroke-dasharray', `${v == null ? 0 : frac(v) * 100} 100`);
        r.set(format(v, unit, w.decimals));
      },
    };
  }

  // ---- bar: horizontal fill
  function bar() {
    const body = el('div', 'barw');
    const r = readout();
    const trackEl = el('div', 'bar-track');
    const fillEl = el('div', 'bar-fill');
    trackEl.append(fillEl);
    const scale = el('div', 'bar-scale');
    const lo = el('span'); const hi = el('span');
    scale.append(lo, hi);
    body.append(r.root, trackEl, scale);
    let tickMax = null;
    return {
      body,
      paint(v) {
        const max = scaleMax();
        if (max !== tickMax) {
          tickMax = max;
          trackEl.querySelectorAll('.bar-tick').forEach((t) => t.remove());
          for (const t of thresholds) {
            if (Number(t.value) <= scaleMin) continue;
            const tick = el('div', 'bar-tick');
            tick.style.left = `calc(${frac(Number(t.value)) * 100}% - 1px)`;
            tick.style.background = t.color;
            trackEl.append(tick);
          }
          const a = format(scaleMin, unit, 0); const b = format(max, unit, 0);
          lo.textContent = a.n; hi.textContent = `${b.n} ${b.u}`;
        }
        fillEl.style.width = `${v == null ? 0 : frac(v) * 100}%`;
        r.set(format(v, unit, w.decimals));
      },
    };
  }

  // ---- graph: rolling line chart on canvas
  function graph() {
    const body = el('div', 'graph');
    const r = readout();
    const canvas = el('canvas');
    body.append(r.root, canvas);
    const ctx = canvas.getContext('2d');
    const hist = [];
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
      const y = (v) => hpx - 2 - frac(v) * (hpx - 4);
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
        if (!started) { ctx.moveTo(x, y(v)); started = true; } else ctx.lineTo(x, y(v));
      });
      ctx.strokeStyle = stroke;
      ctx.lineWidth = 2;
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

/** Picks a starter dashboard from whatever sensors the server has. */
export function suggest(catalog) {
  const out = [];
  const pick = (pred, overrides = {}) => {
    const s = catalog.find(pred);
    if (s && !out.some((w) => w.sensor === s.id && w.type === (overrides.type || defaultsFor(s, catalog).type))) {
      out.push({ ...defaultsFor(s, catalog), ...overrides });
    }
    return s;
  };
  const isCpu = (s) => s.hardwareType === 'Cpu';
  const isGpu = (s) => s.hardwareType.startsWith('Gpu');
  const named = (re) => (s) => re.test(s.name);

  pick((s) => isCpu(s) && s.type === 'Load' && named(/total/i)(s), { label: 'CPU load' });
  pick((s) => isCpu(s) && s.type === 'Temperature' && named(/package|tctl|tdie|core max/i)(s), { label: 'CPU temp' })
    || pick((s) => isCpu(s) && s.type === 'Temperature', { label: 'CPU temp' });
  pick((s) => isGpu(s) && s.type === 'Load' && named(/core/i)(s), { label: 'GPU load' });
  pick((s) => isGpu(s) && s.type === 'Temperature' && named(/core/i)(s), { label: 'GPU temp' });
  // Our own frame counter works on any GPU; AMD's driver FPS (via LibreHardwareMonitor) is the fallback.
  pick((s) => s.id === '/frames/fps', { label: 'FPS' }) || pick((s) => s.type === 'Fps', { label: 'FPS' });
  pick((s) => s.id === '/frames/frametime', { label: 'Frame time', size: 'lg' });
  pick((s) => isCpu(s) && s.type === 'Load' && named(/total/i)(s), { label: 'CPU history', type: 'graph', size: 'lg' });
  pick((s) => s.hardwareType === 'Memory' && s.type === 'Load', { label: 'RAM', type: 'bar' });
  // The driver's own figure first; Windows' D3D counters as a fallback; never shared (system) memory.
  pick((s) => isGpu(s) && /^gpu memory used$/i.test(s.name), { label: 'VRAM used', type: 'bar' })
    || pick((s) => isGpu(s) && /dedicated memory used/i.test(s.name), { label: 'VRAM used', type: 'bar' })
    || pick((s) => isGpu(s) && named(/memory used/i)(s) && !/shared/i.test(s.name), { label: 'VRAM used', type: 'bar' });
  catalog.filter((s) => s.hardwareType === 'Drive' && s.id.endsWith('/used')).slice(0, 4)
    .forEach((s) => out.push({ ...defaultsFor(s, catalog), label: `${s.hardware} used`, type: 'bar' }));
  pick((s) => s.hardwareType === 'Network' && named(/download/i)(s), { label: 'Download', size: 'lg' });
  return out;
}
