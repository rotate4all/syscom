// History chart: one sensor over 1 to 30 days. The line is the average of each time slot; the band behind it spans
// the lowest to highest reading in that slot. Canvas, no libraries. Hover or touch shows the exact values.
import { format } from './widgets.js';

const DAY = 86400;

export class HistoryChart {
  constructor(canvas, tip) {
    this.canvas = canvas;
    this.tip = tip;
    this.data = null;
    this.hover = null;
    this.ro = new ResizeObserver(() => this.draw());
    this.ro.observe(canvas);
    canvas.addEventListener('pointermove', (e) => this.point(e));
    canvas.addEventListener('pointerdown', (e) => this.point(e));
    canvas.addEventListener('pointerleave', (e) => { if (e.pointerType === 'mouse') this.point(null); });
  }

  /**
   * @param series  { step, start, avg[], min[], max[] } from the server, in the sensor's own unit
   * @param opts    { unit, k (unit factor), decimals, thresholds, scaleMin, scaleMax }
   */
  set(series, opts) {
    const k = opts.k || 1;
    const scale = (a) => a.map((v) => (v == null ? null : v * k));
    this.data = { ...series, avg: scale(series.avg), min: scale(series.min), max: scale(series.max), ...opts };
    this.hover = null;
    this.draw();
  }

  /** Lowest, average and highest over the whole range (null when there is no data). */
  stats() {
    const d = this.data;
    if (!d) return null;
    let lo = Infinity, hi = -Infinity, sum = 0, n = 0;
    d.avg.forEach((v, i) => {
      if (v == null) return;
      sum += v; n++;
      lo = Math.min(lo, d.min[i] ?? v);
      hi = Math.max(hi, d.max[i] ?? v);
    });
    return n ? { lo, hi, avg: sum / n, n } : null;
  }

  point(e) {
    const d = this.data;
    if (!d || !e || !this.layout) { this.hover = null; this.draw(); return; }
    const r = this.canvas.getBoundingClientRect();
    const { left, plotW } = this.layout;
    const i = Math.round(((e.clientX - r.left - left) / plotW) * (d.avg.length - 1));
    this.hover = i >= 0 && i < d.avg.length ? i : null;
    this.draw();
  }

  draw() {
    const { canvas } = this;
    const d = this.data;
    const dpr = window.devicePixelRatio || 1;
    const W = canvas.clientWidth, H = canvas.clientHeight;
    if (!W || !H) return;
    if (canvas.width !== Math.round(W * dpr) || canvas.height !== Math.round(H * dpr)) {
      canvas.width = Math.round(W * dpr);
      canvas.height = Math.round(H * dpr);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, W, H);
    this.tip.hidden = true;
    const st = this.stats();
    if (!d || !st) { this.layout = null; return; }

    const css = getComputedStyle(canvas);
    const ink = { line: css.getPropertyValue('--line').trim(), muted: css.getPropertyValue('--muted').trim(),
      accent: css.getPropertyValue('--accent').trim(), surface: css.getPropertyValue('--surface').trim(), text: css.getPropertyValue('--text').trim() };
    ctx.font = '11px system-ui, -apple-system, "Segoe UI", Roboto, sans-serif';

    // Value axis: the widget's fixed scale when it has one, else the data's range; a few round steps.
    const fixedMax = d.scaleMax != null && d.scaleMax !== '' && !isNaN(d.scaleMax) ? Number(d.scaleMax) : null;
    let lo = Math.min(Number(d.scaleMin) || 0, st.lo);
    let hi = fixedMax != null ? Math.max(fixedMax, st.hi) : st.hi;
    if (hi - lo < 1e-9) { hi = lo + 1; }
    const step = niceStep((hi - lo) / 4);
    lo = Math.floor(lo / step) * step;
    hi = Math.ceil(hi / step) * step;
    const ticks = [];
    for (let v = lo; v <= hi + step / 2; v += step) ticks.push(v);
    const label = (v) => { const f = format(v, d.unit, step < 1 ? (step < 0.1 ? 2 : 1) : 0); return `${f.n}${f.u ? ' ' + f.u : ''}`; };
    const left = Math.ceil(Math.max(...ticks.map((v) => ctx.measureText(label(v)).width))) + 10;
    const top = 8, bottom = 22, right = 10;
    const plotW = W - left - right, plotH = H - top - bottom;
    this.layout = { left, plotW };
    const n = d.avg.length;
    const x = (i) => left + (n === 1 ? plotW : (i / (n - 1)) * plotW);
    const y = (v) => top + plotH - ((v - lo) / (hi - lo)) * plotH;

    // Grid and labels: recessive.
    ctx.strokeStyle = ink.line;
    ctx.fillStyle = ink.muted;
    ctx.lineWidth = 1;
    ctx.textAlign = 'right';
    ctx.textBaseline = 'middle';
    for (const v of ticks) {
      const yy = Math.round(y(v)) + 0.5;
      ctx.beginPath(); ctx.moveTo(left, yy); ctx.lineTo(left + plotW, yy); ctx.stroke();
      ctx.fillText(label(v), left - 8, yy);
    }
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    for (const t of timeTicks(d.start, d.step, n, plotW)) {
      const xx = left + ((t.time - d.start) / (d.step * Math.max(1, n - 1))) * plotW;
      const half = ctx.measureText(t.label).width / 2;
      if (xx - half < left - 6 || xx + half > W) continue;   // no label half off the chart or under the value axis
      ctx.fillText(t.label, xx, top + plotH + 6);
    }

    // Thresholds: dashed guides in their own colours.
    ctx.setLineDash([4, 4]);
    for (const t of d.thresholds || []) {
      const v = Number(t.value);
      if (t.value == null || t.value === '' || isNaN(v) || v <= lo || v >= hi) continue;
      ctx.strokeStyle = t.color;
      ctx.globalAlpha = 0.7;
      const yy = Math.round(y(v)) + 0.5;
      ctx.beginPath(); ctx.moveTo(left, yy); ctx.lineTo(left + plotW, yy); ctx.stroke();
    }
    ctx.setLineDash([]);
    ctx.globalAlpha = 1;

    // Lowest-to-highest band, then the average line; gaps (SYSCOM not running) break both.
    const runs = [];
    let run = null;
    d.avg.forEach((v, i) => {
      if (v == null) { run = null; return; }
      if (!run) runs.push(run = []);
      run.push(i);
    });
    ctx.fillStyle = ink.accent;
    ctx.globalAlpha = 0.16;
    for (const r of runs) {
      ctx.beginPath();
      r.forEach((i, j) => (j ? ctx.lineTo(x(i), y(d.max[i] ?? d.avg[i])) : ctx.moveTo(x(i), y(d.max[i] ?? d.avg[i]))));
      for (let j = r.length - 1; j >= 0; j--) ctx.lineTo(x(r[j]), y(d.min[r[j]] ?? d.avg[r[j]]));
      ctx.closePath();
      ctx.fill();
    }
    ctx.globalAlpha = 1;
    ctx.strokeStyle = ink.accent;
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    for (const r of runs) {
      ctx.beginPath();
      r.forEach((i, j) => (j ? ctx.lineTo(x(i), y(d.avg[i])) : ctx.moveTo(x(i), y(d.avg[i]))));
      if (r.length === 1) { ctx.arc(x(r[0]), y(d.avg[r[0]]), 2, 0, Math.PI * 2); ctx.fillStyle = ink.accent; ctx.fill(); }
      ctx.stroke();
    }

    // Crosshair and tooltip.
    const i = this.hover;
    if (i != null) {
      const xx = Math.round(x(i)) + 0.5;
      ctx.strokeStyle = ink.muted;
      ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(xx, top); ctx.lineTo(xx, top + plotH); ctx.stroke();
      const v = d.avg[i];
      if (v != null) {
        ctx.beginPath();
        ctx.arc(x(i), y(v), 4, 0, Math.PI * 2);
        ctx.fillStyle = ink.accent;
        ctx.fill();
        ctx.lineWidth = 2;
        ctx.strokeStyle = ink.surface;
        ctx.stroke();
      }
      const f = (val) => { const o = format(val, d.unit, d.decimals); return `${o.n}${o.u ? ' ' + o.u : ''}`; };
      const from = new Date((d.start + i * d.step) * 1000);
      this.tip.innerHTML = `<div class="t">${when(from, d.step)}</div>` + (v == null
        ? '<div>No data</div>'
        : `<div><b>${f(v)}</b> average</div><div class="sub">${f(d.min[i] ?? v)} to ${f(d.max[i] ?? v)}</div>`);
      this.tip.hidden = false;
      const tw = this.tip.offsetWidth;
      this.tip.style.left = `${Math.min(Math.max(xx - tw / 2, 0), W - tw)}px`;
      this.tip.style.top = `${top}px`;
    }
  }

  destroy() { this.ro.disconnect(); }
}

function niceStep(raw) {
  if (!(raw > 0)) return 1;
  const p = 10 ** Math.floor(Math.log10(raw));
  for (const m of [1, 2, 2.5, 5, 10]) if (m * p >= raw) return m * p;
  return 10 * p;
}

/** Time labels on local-time boundaries: hours for short ranges, dates for long ones. */
function timeTicks(start, step, n, width) {
  const end = start + step * (n - 1);
  const span = end - start;
  const fits = Math.max(2, Math.floor(width / 72));
  const H = 3600;
  const choice = [H, 2 * H, 3 * H, 6 * H, 12 * H, DAY, 2 * DAY, 7 * DAY].find((s) => span / s <= fits) || 7 * DAY;
  const out = [];
  const t0 = new Date(start * 1000);
  // First boundary at or after the start, in local time.
  const first = new Date(t0);
  if (choice < DAY) {
    first.setMinutes(0, 0, 0);
    while (first.getTime() / 1000 < start || first.getHours() % (choice / H)) first.setHours(first.getHours() + 1);
  } else {
    first.setHours(0, 0, 0, 0);
    if (first.getTime() / 1000 < start) first.setDate(first.getDate() + 1);
  }
  for (const d = first; d.getTime() / 1000 <= end; choice < DAY ? d.setHours(d.getHours() + choice / H) : d.setDate(d.getDate() + choice / DAY)) {
    const label = choice < DAY
      ? (d.getHours() === 0 ? d.toLocaleDateString(undefined, { weekday: 'short' }) : d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }))
      : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    out.push({ time: d.getTime() / 1000, label });
  }
  return out;
}

function when(date, step) {
  const day = date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  const time = (d) => d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  return step >= 3600 ? `${day}, ${time(date)}` : `${day}, ${time(date)} to ${time(new Date(date.getTime() + step * 1000))}`;
}
