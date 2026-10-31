import { load, save, normalizeDevice, normalizeUrl, exportLayout, uid, MIN_INTERVAL } from './store.js';
import { Connection } from './connection.js';
import { createWidget, defaultsFor, suggest, COLORS, unitChoices, unitFactor, totalFor, effectiveMaxFrom } from './widgets.js';

const $ = (s, root = document) => root.querySelector(s);
const state = load();
const conns = new Map();   // device.key → Connection
const pages = new Map();   // device.key → { section, grid, msg, widgets: Map(widgetId → instance) }
let active = 0;
let editing = false;

const ICON = {
  left: '<svg viewBox="0 0 24 24"><path d="m15 6-6 6 6 6"/></svg>',
  right: '<svg viewBox="0 0 24 24"><path d="m9 6 6 6-6 6"/></svg>',
  edit: '<svg viewBox="0 0 24 24"><path d="M4 20h4l10.5-10.5a2.1 2.1 0 0 0-4-4L4 16v4Z"/></svg>',
  del: '<svg viewBox="0 0 24 24"><path d="M5 7h14M10 11v6M14 11v6M6 7l1 12h10l1-12M9 7V4h6v3"/></svg>',
};

const persist = () => save({ ...state, active });

// ---------- theme
function applyTheme() {
  if (state.theme === 'auto') delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = state.theme;
  document.querySelectorAll('#theme button').forEach((b) => b.classList.toggle('active', b.dataset.theme === state.theme));
  const dark = state.theme === 'dark' || (state.theme === 'auto' && matchMedia('(prefers-color-scheme: dark)').matches);
  $('meta[name="theme-color"]').content = dark ? '#0e1116' : '#f4f6f9';
  pages.forEach((p) => p.widgets.forEach((w) => w.refresh()));
}

// ---------- devices & connections
function connectionFor(dev) {
  let c = conns.get(dev.key);
  if (c) return c;
  c = new Connection(dev);
  c.addEventListener('status', () => { renderMessage(dev); updateHeader(); });
  c.addEventListener('token', persist);
  c.addEventListener('catalog', () => { if (!refreshPage(dev)) renderPage(dev); c.subscribe(); });
  c.addEventListener('data', (e) => onData(dev, e.detail));
  conns.set(dev.key, c);
  return c;
}

function onData(dev, frame) {
  const page = pages.get(dev.key);
  if (!page) return;
  const conn = conns.get(dev.key);
  const visible = state.devices[active] === dev && document.visibilityState === 'visible';
  for (const w of dev.widgets) {
    const inst = page.widgets.get(w.id);
    if (!inst) continue;
    const max = inst.maxFrom ? inst.maxFrom.reduce((sum, id) => {
      const x = conn.value(frame, id);
      return sum == null || x == null ? null : sum + x;
    }, 0) : null;
    inst.update(conn.value(frame, w.sensor), visible, max);
  }
}

async function addDevice(url, { quiet = false } = {}) {
  const normalized = normalizeUrl(url);
  const probe = new Connection({ url: normalized, widgets: [] });
  probe.device.httpsPort = normalized.startsWith('https:') ? Number(new URL(normalized).port) : null;
  const info = await probe.info();
  // Store the plain HTTP address plus the HTTPS port; the connection picks whichever this page needs.
  const host = new URL(normalized).hostname;
  const httpUrl = `http://${host}:${info.port || 47810}`;
  const existing = state.devices.find((d) => d.serverId === info.id || d.url === httpUrl);
  if (existing) {
    if (info.httpsPort && existing.httpsPort !== info.httpsPort) { existing.httpsPort = info.httpsPort; persist(); }
    if (!quiet) goTo(state.devices.indexOf(existing));
    return existing;
  }
  const dev = normalizeDevice({ key: uid(), serverId: info.id, url: httpUrl, httpsPort: info.httpsPort, name: info.name });
  state.devices.push(dev);
  persist();
  renderPages();
  connectionFor(dev).connect();
  if (!quiet) goTo(state.devices.length - 1);
  return dev;
}

function removeDevice(dev) {
  conns.get(dev.key)?.stop();
  conns.delete(dev.key);
  state.devices = state.devices.filter((d) => d !== dev);
  persist();
  renderPages();
}

// ---------- rendering
function renderPages() {
  const host = $('#pages');
  const keep = new Set(state.devices.map((d) => d.key));
  for (const [key, p] of pages) {
    if (!keep.has(key)) { p.widgets.forEach((w) => w.destroy()); p.section.remove(); pages.delete(key); }
  }
  state.devices.forEach((dev) => {
    let p = pages.get(dev.key);
    if (!p) {
      const section = document.createElement('section');
      section.className = 'page';
      const msg = document.createElement('div');
      msg.className = 'page-msg';
      const grid = document.createElement('div');
      grid.className = 'grid';
      section.append(msg, grid);
      p = { section, msg, grid, widgets: new Map() };
      pages.set(dev.key, p);
      renderPage(dev);
    }
    host.append(p.section); // re-append keeps DOM order in sync with state
  });

  if (!state.devices.length) {
    host.innerHTML = '';
    const empty = document.createElement('section');
    empty.className = 'page';
    empty.innerHTML = '<div class="empty"><p><strong>No devices yet.</strong></p><p>Add a SYSCOM server from Settings.</p></div>';
    host.append(empty);
  }
  active = Math.min(active, Math.max(0, state.devices.length - 1));
  renderDots();
  updateHeader();
}

function renderPage(dev) {
  const p = pages.get(dev.key);
  if (!p) return;
  const conn = connectionFor(dev);
  p.widgets.forEach((w) => w.destroy());
  p.widgets.clear();
  p.grid.replaceChildren();

  dev.widgets.forEach((w, i) => {
    const sensor = conn.sensors.get(w.sensor);
    const inst = createWidget(w, sensor, widgetTools(dev, w, i));
    inst.maxFrom = effectiveMaxFrom(w, sensor, conn.catalog);
    inst.signature = widgetSignature(w, conn);
    p.widgets.set(w.id, inst);
    p.grid.append(inst.el);
  });

  const add = document.createElement('button');
  add.className = 'add-tile';
  add.textContent = '+ Add widget';
  add.onclick = () => openWidgetEditor(dev, null);
  p.grid.append(add);
  if (dev.widgets.length) {
    const reset = document.createElement('button');
    reset.className = 'add-tile';
    reset.textContent = '↺ Suggested layout';
    reset.onclick = () => {
      if (!conn.catalog.length || !confirm(`Replace the ${dev.name} dashboard with the suggested layout?`)) return;
      dev.widgets = suggest(conn.catalog).map((w) => ({ ...w, id: uid() }));
      persist(); renderPage(dev); conn.subscribe();
    };
    p.grid.append(reset);
  }
  renderMessage(dev);
}

function widgetSignature(w, conn) {
  const sensor = conn.sensors.get(w.sensor);
  return `${sensor?.unit ?? '-'}|${(effectiveMaxFrom(w, sensor, conn.catalog) || []).join(',')}`;
}

/**
 * A new catalog (reconnect, or the game shown by the frame counter changed) usually leaves every widget's sensor in
 * place. Then only the names are updated, so readings and graph history stay on screen. Returns false when the
 * page has to be rebuilt.
 */
function refreshPage(dev) {
  const p = pages.get(dev.key);
  const conn = conns.get(dev.key);
  if (!p || !conn || p.widgets.size !== dev.widgets.length) return false;
  for (const w of dev.widgets) {
    const inst = p.widgets.get(w.id);
    if (!inst || inst.signature !== widgetSignature(w, conn)) return false;
  }
  for (const w of dev.widgets) p.widgets.get(w.id).setSensor(conn.sensors.get(w.sensor));
  renderMessage(dev);
  return true;
}

function widgetTools(dev, w, i) {
  const tools = document.createElement('div');
  tools.className = 'card-tools';
  const btn = (html, title, fn, cls = '') => {
    const b = document.createElement('button');
    b.innerHTML = html; b.title = title; b.className = cls; b.onclick = fn;
    tools.append(b);
  };
  const move = (d) => () => {
    const j = i + d;
    if (j < 0 || j >= dev.widgets.length) return;
    [dev.widgets[i], dev.widgets[j]] = [dev.widgets[j], dev.widgets[i]];
    persist(); renderPage(dev);
  };
  btn(ICON.left, 'Move earlier', move(-1));
  btn(ICON.right, 'Move later', move(1));
  btn(ICON.edit, 'Edit', () => openWidgetEditor(dev, w));
  btn(ICON.del, 'Remove', () => {
    dev.widgets = dev.widgets.filter((x) => x !== w);
    persist(); renderPage(dev); conns.get(dev.key)?.subscribe();
  }, 'del');
  return tools;
}

function renderMessage(dev) {
  const p = pages.get(dev.key);
  if (!p) return;
  const conn = conns.get(dev.key);
  p.msg.replaceChildren();
  p.section.classList.toggle('stale', conn?.status === 'offline');

  if (conn?.status === 'auth') {
    const form = $('#tpl-login').content.firstElementChild.cloneNode(true);
    $('[data-name]', form).textContent = dev.name;
    form.onsubmit = async (e) => {
      e.preventDefault();
      const err = $('.error', form);
      err.hidden = true;
      try { await conn.login(form.password.value); }
      catch (ex) { err.textContent = ex.message; err.hidden = false; }
    };
    p.msg.append(form);
    p.grid.hidden = true;
    return;
  }
  p.grid.hidden = false;

  if (conn?.status === 'offline') {
    p.msg.innerHTML = `<div class="notice"><strong>Can't reach ${escapeHtml(dev.name)}.</strong> Retrying… <span class="sub">${escapeHtml(dev.url)}</span></div>`;
  } else if (!dev.widgets.length && conn?.status === 'online') {
    const box = document.createElement('div');
    box.className = 'empty';
    box.innerHTML = '<p><strong>This dashboard is empty.</strong></p><p>Start with a suggested layout or add widgets one by one.</p><div class="inline"><button class="primary" data-a="quick">Suggested layout</button><button class="ghost" data-a="add">Add widget</button></div>';
    $('[data-a="quick"]', box).onclick = () => {
      dev.widgets = suggest(conn.catalog).map((w) => ({ ...w, id: uid() }));
      persist(); renderPage(dev); conn.subscribe();
    };
    $('[data-a="add"]', box).onclick = () => openWidgetEditor(dev, null);
    p.msg.append(box);
  }
}

function renderDots() {
  const nav = $('#dots');
  nav.replaceChildren();
  if (state.devices.length < 2) return;
  state.devices.forEach((d, i) => {
    const b = document.createElement('button');
    b.title = d.name;
    b.className = i === active ? 'active' : '';
    b.onclick = () => goTo(i);
    nav.append(b);
  });
}

function updateHeader() {
  const dev = state.devices[active];
  $('#device-name').textContent = dev ? dev.name : 'SYSCOM';
  const status = dev ? conns.get(dev.key)?.status || 'idle' : 'idle';
  $('#status').className = 'dot ' + status;
  $('#status').title = status;
}

function goTo(i) {
  const host = $('#pages');
  host.scrollTo({ left: i * host.clientWidth, behavior: 'smooth' });
  setActive(i);
}

function setActive(i) {
  if (i === active && $('#dots .active')) return;
  active = i;
  persist();
  renderDots();
  updateHeader();
  const dev = state.devices[active];
  if (dev) pages.get(dev.key)?.widgets.forEach((w) => w.refresh());
}

// ---------- swipe tracking (native scroll-snap does the gesture)
let scrollRaf = 0;
$('#pages').addEventListener('scroll', () => {
  cancelAnimationFrame(scrollRaf);
  scrollRaf = requestAnimationFrame(() => {
    const host = $('#pages');
    const i = Math.round(host.scrollLeft / (host.clientWidth || 1));
    if (i !== active && i < state.devices.length) setActive(i);
  });
}, { passive: true });

addEventListener('keydown', (e) => {
  if (document.querySelector('dialog[open]') || e.target.matches('input, select')) return;
  if (e.key === 'ArrowRight' && active < state.devices.length - 1) goTo(active + 1);
  if (e.key === 'ArrowLeft' && active > 0) goTo(active - 1);
});

addEventListener('resize', () => {
  const host = $('#pages');
  host.scrollTo({ left: active * host.clientWidth });
});

// Streams keep running while the tab is in the background, so readings and graphs are current the moment you
// look again (hidden pages just skip painting). Phones may still drop the connection while asleep; reconnect
// right away instead of waiting for the backoff timer.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState !== 'visible') return;
  state.devices.forEach((d) => {
    const c = connectionFor(d);
    if (c.status === 'offline' || c.status === 'idle') { c.retry = 0; c.connect(); }
  });
  const dev = state.devices[active];
  if (dev) pages.get(dev.key)?.widgets.forEach((w) => w.refresh());
});

// ---------- edit mode
$('#btn-edit').onclick = () => {
  editing = !editing;
  document.body.classList.toggle('editing', editing);
  $('#btn-edit').setAttribute('aria-pressed', String(editing));
};

// ---------- widget editor
const dlgWidget = $('#dlg-widget');
let editTarget = null;

// Tabs for which part of the PC, pills under them for which kind of reading. Keeps the sensor list short.
const DEVICE_CATS = [
  ['cpu', 'CPU', (s) => s.hardwareType === 'Cpu'],
  ['gpu', 'GPU', (s) => s.hardwareType.startsWith('Gpu')],
  ['game', 'Game', (s) => s.hardwareType === 'Game'],
  ['mem', 'Memory', (s) => s.hardwareType === 'Memory'],
  ['disk', 'Drives', (s) => s.hardwareType === 'Storage' || s.hardwareType === 'Drive'],
  ['net', 'Network', (s) => s.hardwareType === 'Network'],
  ['board', 'Motherboard', (s) => ['Motherboard', 'SuperIO', 'EmbeddedController', 'Cooler', 'Psu', 'Battery'].includes(s.hardwareType)],
];
const READING_CATS = [
  ['temp', 'Temperature', ['Temperature']],
  ['load', 'Load', ['Load', 'Level', 'Factor']],
  ['clock', 'Clock', ['Clock', 'Frequency']],
  ['power', 'Power', ['Power', 'Voltage', 'Current', 'Energy']],
  ['data', 'Memory & space', ['Data', 'SmallData']],
  ['fan', 'Fans', ['Fan', 'Control', 'Flow']],
  ['rate', 'Data rate', ['Throughput']],
  ['fps', 'Frames', ['Fps', 'Frametime']],
];
// Friendlier names for LibreHardwareMonitor's sensor types in the list.
const TYPE_LABEL = { Data: 'Size', SmallData: 'Size', Throughput: 'Data rate', Fps: 'Frame rate', Frametime: 'Frame time', Control: 'Fan control' };
const deviceCat = (s) => DEVICE_CATS.find(([, , test]) => test(s))?.[0] || 'other';
const readingCat = (s) => READING_CATS.find(([, , types]) => types.includes(s.type))?.[0] || 'other';
const pick = { dev: 'all', read: 'all' };
let unitShown = null;   // the size unit the form's numbers are in

function openWidgetEditor(dev, widget) {
  const conn = conns.get(dev.key);
  if (!conn?.catalog.length) { alert('Connect to this device first to see its sensors.'); return; }
  editTarget = { dev, widget };
  $('#widget-title').textContent = widget ? 'Edit widget' : 'Add widget';
  $('#w-search').value = '';
  const current = widget && conn.sensors.get(widget.sensor);
  if (current) { pick.dev = deviceCat(current); pick.read = readingCat(current); }
  else { pick.dev = DEVICE_CATS.find(([, , test]) => conn.catalog.some(test))?.[0] || 'other'; pick.read = 'all'; }
  fillSensorList(conn.catalog, widget?.sensor);
  const sensor = conn.sensors.get($('#w-sensor').value);
  fillWidgetForm(widget || defaultsFor(sensor || conn.catalog[0], conn.catalog), sensor, conn.catalog);
  dlgWidget.showModal();
  showActiveTab();
}

/** Device tabs (no "All": search covers that) or reading pills (with "All"). Empty categories are left out. */
function renderChoices(host, cats, current, counts, onPick, { tabs = false } = {}) {
  host.replaceChildren();
  const all = [...counts.values()].reduce((a, b) => a + b, 0);
  const keys = [...(tabs ? [] : [['all', 'All']]), ...cats.map(([k, l]) => [k, l]), ['other', 'Other']];
  for (const [key, label] of keys) {
    const n = key === 'all' ? all : counts.get(key) || 0;
    if (!n) continue;
    const b = document.createElement('button');
    b.type = 'button';
    b.className = key === current ? 'active' : '';
    if (tabs) { b.setAttribute('role', 'tab'); b.setAttribute('aria-selected', String(key === current)); b.title = `${n} sensors`; b.textContent = label; }
    else { b.setAttribute('aria-pressed', String(key === current)); b.innerHTML = `${escapeHtml(label)}<span class="n">${n}</span>`; }
    b.onclick = () => onPick(key);
    host.append(b);
  }
  if (tabs) showActiveTab();
}

/** Keeps the chosen tab in view and fades the row's ends when more tabs are hidden past them. */
function showActiveTab() {
  const tabs = $('#w-dev');
  tabs.querySelector('.active')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  tabEdges();
}
function tabEdges() {
  const tabs = $('#w-dev');
  const picker = $('#w-picker');
  picker.style.setProperty('--tabs-h', `${tabs.offsetHeight}px`);
  picker.classList.toggle('more-l', tabs.scrollLeft > 2);
  picker.classList.toggle('more-r', tabs.scrollLeft + tabs.clientWidth < tabs.scrollWidth - 2);
}
$('#w-dev').addEventListener('scroll', tabEdges, { passive: true });
// A mouse wheel scrolls the tab row sideways when it doesn't fit.
$('#w-dev').addEventListener('wheel', (e) => {
  const tabs = e.currentTarget;
  if (tabs.scrollWidth <= tabs.clientWidth || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
  tabs.scrollLeft += e.deltaY;
  e.preventDefault();
}, { passive: false });
window.addEventListener('resize', () => { if (dlgWidget.open) tabEdges(); });

function fillSensorList(catalog, selectedId) {
  const sel = $('#w-sensor');
  const q = $('#w-search').value.trim().toLowerCase();
  const searching = q.length > 0;

  // Device tabs count everything; reading pills count what's in the chosen device.
  const devCounts = new Map();
  for (const s of catalog) devCounts.set(deviceCat(s), (devCounts.get(deviceCat(s)) || 0) + 1);
  if (!devCounts.get(pick.dev)) pick.dev = [...DEVICE_CATS.map(([k]) => k), 'other'].find((k) => devCounts.get(k));
  const inDev = catalog.filter((s) => deviceCat(s) === pick.dev);
  const readCounts = new Map();
  for (const s of inDev) readCounts.set(readingCat(s), (readCounts.get(readingCat(s)) || 0) + 1);
  if (pick.read !== 'all' && !readCounts.get(pick.read)) pick.read = 'all';

  const repick = () => {
    $('#w-search').value = '';
    fillSensorList(catalog, null);
    onSensorPicked();
  };
  renderChoices($('#w-dev'), DEVICE_CATS, pick.dev, devCounts, (k) => { pick.dev = k; pick.read = 'all'; repick(); }, { tabs: true });
  renderChoices($('#w-read'), READING_CATS, pick.read, readCounts, (k) => { pick.read = k; repick(); });
  $('#w-picker').classList.toggle('searching', searching);
  $('#w-read').hidden = searching || readCounts.size < 2;

  // Searching looks through every sensor; otherwise the tab and pill decide.
  const list = searching
    ? catalog.filter((s) => q.split(/\s+/).every((part) => `${s.hardware} ${s.name} ${s.type}`.toLowerCase().includes(part)))
    : inDev.filter((s) => pick.read === 'all' || readingCat(s) === pick.read);

  sel.replaceChildren();
  const groups = new Map();
  for (const s of list) {
    if (!groups.has(s.hardware)) groups.set(s.hardware, []);
    groups.get(s.hardware).push(s);
  }
  for (const [hw, items] of groups) {
    const og = document.createElement('optgroup');
    og.label = hw;
    for (const s of items) {
      const o = document.createElement('option');
      o.value = s.id;
      o.textContent = `${s.name} · ${TYPE_LABEL[s.type] || s.type}${s.unit ? ` (${s.unit})` : ''}`;
      if (s.id === selectedId) o.selected = true;
      og.append(o);
    }
    sel.append(og);
  }
  if (!sel.value && sel.options.length) sel.options[0].selected = true;
}

function fillWidgetForm(w, sensor, catalog) {
  $('#w-type').value = w.type;
  $('#w-size').value = w.size || 'sm';
  $('#w-label').value = w.label || '';
  $('#w-min').value = w.min ?? '';
  $('#w-max').value = w.max ?? '';
  $('#w-dec').value = String(w.decimals ?? 0);
  const fallback = [COLORS.min, COLORS.med, COLORS.max];
  for (let i = 0; i < 3; i++) {
    const t = w.thresholds?.[i] || {};
    $(`#t${i}-v`).value = t.value ?? '';
    $(`#t${i}-c`).value = t.color || fallback[i];
  }

  // GB / MB for memory and storage sizes. The form's numbers are in the unit shown.
  const choices = unitChoices(sensor);
  $('#w-unit-field').hidden = !choices;
  unitShown = choices ? (w.unit && choices.includes(w.unit) ? w.unit : sensor.unit) : null;
  if (choices) $('#w-unit').value = unitShown;

  // "Scale max follows the total": live total VRAM / RAM / drive size instead of a fixed number.
  const total = totalFor(sensor, catalog);
  $('#w-total-field').hidden = !total;
  $('#w-total').checked = !!total && !!effectiveMaxFrom(w, sensor, catalog);
  $('#w-total-hint').textContent = total ? `(${total.map((id) => conns.get(editTarget.dev.key)?.sensors.get(id)?.name).join(' + ')})` : '';
  syncTotal();
}

function syncTotal() {
  const on = !$('#w-total-field').hidden && $('#w-total').checked;
  $('#w-max').disabled = on;
  $('#w-max').placeholder = on ? 'total' : 'auto';
  if (on) $('#w-max').value = '';
}

function onSensorPicked() {
  if (!editTarget) return;
  const conn = conns.get(editTarget.dev.key);
  const s = conn?.sensors.get($('#w-sensor').value);
  if (!s) return;
  // Adding: a different sensor brings its own defaults. Editing: keep the settings, refresh the sensor-specific bits.
  if (!editTarget.widget) fillWidgetForm(defaultsFor(s, conn.catalog), s, conn.catalog);
  else fillWidgetForm(readForm(editTarget.widget.id), s, conn.catalog);
}

$('#w-search').addEventListener('input', () => {
  const conn = conns.get(editTarget?.dev.key);
  if (!conn) return;
  const before = $('#w-sensor').value;
  fillSensorList(conn.catalog, before);
  if ($('#w-sensor').value !== before) onSensorPicked();
});

$('#w-sensor').addEventListener('change', onSensorPicked);
$('#w-total').addEventListener('change', syncTotal);

// Switching GB ↔ MB converts the numbers already in the form.
$('#w-unit').addEventListener('change', () => {
  const next = $('#w-unit').value;
  const f = unitFactor(unitShown, next);
  for (const id of ['#w-min', '#w-max', '#t0-v', '#t1-v', '#t2-v']) {
    const v = $(id).value;
    if (v !== '') $(id).value = String(+(Number(v) * f).toFixed(3));
  }
  $('#w-dec').value = next === 'MB' ? '0' : '1';
  unitShown = next;
});

function readForm(id) {
  const num = (sel) => { const v = $(sel).value; return v === '' ? null : Number(v); };
  const conn = conns.get(editTarget.dev.key);
  const sensor = conn?.sensors.get($('#w-sensor').value);
  const total = totalFor(sensor, conn?.catalog);
  const useTotal = !!total && $('#w-total').checked;
  return {
    id,
    sensor: $('#w-sensor').value,
    type: $('#w-type').value,
    size: $('#w-size').value,
    label: $('#w-label').value.trim(),
    min: num('#w-min') ?? 0,
    max: useTotal ? null : num('#w-max'),
    maxFrom: useTotal ? total : [],
    unit: unitChoices(sensor) ? $('#w-unit').value : undefined,
    decimals: Number($('#w-dec').value),
    thresholds: [0, 1, 2].map((i) => ({ value: num(`#t${i}-v`), color: $(`#t${i}-c`).value })),
  };
}

dlgWidget.addEventListener('close', () => {
  if (dlgWidget.returnValue !== 'save' || !editTarget) return;
  const { dev, widget } = editTarget;
  const next = readForm(widget?.id || uid());
  if (!next.sensor) return;
  if (widget) dev.widgets[dev.widgets.indexOf(widget)] = next;
  else dev.widgets.push(next);
  persist();
  renderPage(dev);
  conns.get(dev.key)?.subscribe();
  editTarget = null;
});

// ---------- settings
const dlgSettings = $('#dlg-settings');
$('#btn-settings').onclick = () => { renderSettings(); dlgSettings.showModal(); };
dlgSettings.querySelector('[data-close]').onclick = () => dlgSettings.close();
dlgSettings.addEventListener('click', (e) => { if (e.target === dlgSettings) dlgSettings.close(); });

$('#theme').addEventListener('click', (e) => {
  const t = e.target.closest('button')?.dataset.theme;
  if (!t) return;
  state.theme = t;
  persist();
  applyTheme();
});

function renderSettings() {
  applyTheme();
  const list = $('#device-list');
  list.replaceChildren();
  state.devices.forEach((dev, i) => {
    const conn = conns.get(dev.key);
    const li = document.createElement('li');
    li.innerHTML = `
      <span class="dot ${conn?.status || 'idle'}"></span>
      <span class="grow"><input class="name" value="${escapeHtml(dev.name)}" aria-label="Device name"><span class="sub">${escapeHtml(conn?.base || dev.url)}${conn?.secure ? ' · secure' : ''}</span></span>
      <label class="interval">Every <input type="number" min="${MIN_INTERVAL}" step="100" value="${dev.interval}"> ms</label>
      <button data-a="up" title="Move up">↑</button>
      ${dev.token ? '<button data-a="logout">Sign out</button>' : ''}
      <button data-a="remove" class="danger">Remove</button>`;
    $('input.name', li).onchange = (e) => { dev.name = e.target.value.trim() || dev.name; persist(); updateHeader(); renderDots(); };
    $('.interval input', li).onchange = (e) => {
      dev.interval = Math.max(MIN_INTERVAL, Math.round(Number(e.target.value) || 1000));
      e.target.value = dev.interval;
      persist();
      conn?.subscribe();
    };
    $('[data-a="up"]', li).onclick = () => {
      if (i === 0) return;
      [state.devices[i - 1], state.devices[i]] = [state.devices[i], state.devices[i - 1]];
      persist(); renderPages(); renderSettings();
    };
    $('[data-a="logout"]', li)?.addEventListener('click', () => { conn?.logout(); persist(); renderSettings(); });
    $('[data-a="remove"]', li).onclick = () => {
      if (confirm(`Remove ${dev.name} and its dashboard from this browser?`)) { removeDevice(dev); renderSettings(); }
    };
    list.append(li);
  });
  renderPeers();
  renderAppInstall();
}

function renderAppInstall() {
  const box = $('#app-install');
  const dev = state.devices[active];
  if (location.protocol === 'https:') {
    box.innerHTML = '<p class="hint"><strong>Secure connection.</strong> Add this page to your home screen to use SYSCOM as an app.</p>';
  } else if (dev?.httpsPort) {
    const trust = new URL('/trust', dev.url).href;
    box.innerHTML = `<p class="hint">To install SYSCOM as an app on this phone, trust this PC's certificate once.</p>
      <div class="inline"><a class="ghost button" href="${escapeHtml(trust)}">Set up app install</a></div>`;
  } else {
    box.innerHTML = '<p class="hint">HTTPS is turned off on this PC, so the dashboard can only be added as a browser shortcut.</p>';
  }
}

async function renderPeers() {
  const list = $('#peer-list');
  const known = new Set(state.devices.map((d) => d.serverId));
  const seen = new Map();
  await Promise.all([...conns.values()].map(async (c) => {
    try {
      for (const p of await c.peers()) {
        // From an HTTPS dashboard only HTTPS peers are reachable.
        const url = location.protocol === 'https:' ? p.httpsUrl : p.url;
        if (!known.has(p.id) && url) seen.set(p.id, { ...p, url });
      }
    } catch { /* offline */ }
  }));
  list.replaceChildren();
  for (const p of seen.values()) {
    const li = document.createElement('li');
    li.innerHTML = `<span class="grow">${escapeHtml(p.name)}<span class="sub">Found on your network · ${escapeHtml(p.url)}</span></span><button>Add</button>`;
    $('button', li).onclick = async () => {
      try { await addDevice(p.url); renderSettings(); } catch (ex) { showAddError(ex); }
    };
    list.append(li);
  }
}

$('#add-device').onsubmit = async (e) => {
  e.preventDefault();
  $('#add-error').hidden = true;
  try {
    await addDevice($('#add-url').value);
    $('#add-url').value = '';
    renderSettings();
  } catch (ex) {
    showAddError(ex);
  }
};

function showAddError(ex) {
  $('#add-error').textContent = ex instanceof TypeError
    ? 'Could not reach that address. Check the IP and port, and that SYSCOM is running.'
    : ex.message;
  $('#add-error').hidden = false;
}

$('#btn-export').onclick = () => {
  const blob = new Blob([exportLayout(state)], { type: 'application/json' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = 'syscom-layout.json';
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
};

$('#import-file').onchange = async (e) => {
  const file = e.target.files[0];
  e.target.value = '';
  if (!file) return;
  try {
    const data = JSON.parse(await file.text());
    if (data.app !== 'syscom' || !Array.isArray(data.devices)) throw new Error('Not a SYSCOM layout file.');
    for (const d of data.devices) {
      const existing = state.devices.find((x) => (d.serverId && x.serverId === d.serverId) || x.url === d.url);
      if (existing) { existing.widgets = d.widgets || []; existing.interval = d.interval || existing.interval; existing.name = d.name || existing.name; }
      else state.devices.push(normalizeDevice({ ...d, key: uid(), token: null }));
    }
    if (data.theme) state.theme = data.theme;
    persist();
    pages.forEach((p) => p.section.remove());
    pages.clear();
    renderPages();
    state.devices.forEach((d) => { connectionFor(d).connect(); renderPage(d); });
    renderSettings();
  } catch (ex) {
    alert('Import failed: ' + ex.message);
  }
};

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ---------- start
// Installable app: service workers only run on HTTPS or localhost; over plain LAN HTTP the page still works.
if ('serviceWorker' in navigator && window.isSecureContext) {
  navigator.serviceWorker.register('sw.js').catch(() => { /* not fatal */ });
}

matchMedia('(prefers-color-scheme: dark)').addEventListener('change', applyTheme);
applyTheme();

(async function start() {
  // First visit: add the server that served this page.
  if (!state.devices.length && location.protocol.startsWith('http')) {
    try { await addDevice(location.origin, { quiet: true }); } catch { /* opened from elsewhere */ }
  }
  renderPages();
  state.devices.forEach((d) => connectionFor(d).connect());
  active = Math.min(state.active || 0, Math.max(0, state.devices.length - 1));
  requestAnimationFrame(() => { $('#pages').scrollTo({ left: active * $('#pages').clientWidth }); renderDots(); updateHeader(); });
  if (!state.devices.length) { renderSettings(); dlgSettings.showModal(); }
})();
