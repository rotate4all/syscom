import {
  load, save, normalizeDevice, normalizeLayouts, normalizeUrl, exportLayout, uid, MIN_INTERVAL,
  layoutMode, setLayoutMode, adoptPhoneView,
} from './store.js';
import { Connection } from './connection.js';
import {
  createWidget, defaultsFor, suggest, COLORS, unitChoices, unitFactor, unitLabel, decimalsFor, totalFor, effectiveMaxFrom, displayUnit,
  isSection, defaultSize, positionsFor, defaultPosition, GAUGE_STYLES, MAX_W, MAX_H, format, thicknessOf,
  MAX_EXTRA, EXTRA_SHOW, EXTRA_ALIGN, EXTRA_SIZE, NUMBER_SIZE, UNIT_POS, unitPosOf, MAX_EXTRA_LABEL, extraLabel, extrasOf, readingText, HISTORY,
} from './widgets.js';
import { enableDnd } from './dnd.js';
import { HistoryChart } from './history.js';

const $ = (s, root = document) => root.querySelector(s);
const state = load();
const conns = new Map();   // device.key → Connection
const pages = new Map();   // device.key → { section, board, msg, widgets: Map(widgetId → instance) }
let active = 0;
let editing = false;

const ICON = {
  grip: '<svg viewBox="0 0 24 24"><path d="M9 5h.01M15 5h.01M9 12h.01M15 12h.01M9 19h.01M15 19h.01" stroke-width="3.2"/></svg>',
  edit: '<svg viewBox="0 0 24 24"><path d="M4 20h4l10.5-10.5a2.1 2.1 0 0 0-4-4L4 16v4Z"/></svg>',
  del: '<svg viewBox="0 0 24 24"><path d="M5 7h14M10 11v6M14 11v6M6 7l1 12h10l1-12M9 7V4h6v3"/></svg>',
  resize: '<svg viewBox="0 0 24 24"><path d="M20 11v9h-9M20 20 12 12"/></svg>',
  chevron: '<svg viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>',
};
const ROW_LIMIT = MAX_H;

const persist = () => save({ ...state, active });

// ---------- theme
function applyTheme() {
  if (state.theme === 'auto') delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = state.theme;
  document.querySelectorAll('#theme button').forEach((b) => b.classList.toggle('active', b.dataset.theme === state.theme));
  const dark = state.theme === 'dark' || (state.theme === 'auto' && matchMedia('(prefers-color-scheme: dark)').matches);
  $('meta[name="theme-color"]').content = dark ? '#0e1116' : '#f4f6f9';
  pages.forEach((p) => p.widgets.forEach((w) => w.refresh()));
  if (dlgHistory.open) chart.draw();
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
  c.addEventListener('layout', (e) => syncLayout(dev, e.detail.rev));
  conns.set(dev.key, c);
  return c;
}

function onData(dev, frame) {
  const page = pages.get(dev.key);
  if (!page) return;
  const conn = conns.get(dev.key);
  // Recent readings for graphs, kept per page so rebuilt cards keep their history.
  for (const [id, i] of conn.index) {
    const a = page.series.get(id);
    if (!a) continue;
    a.push(frame.v[i]);
    if (a.length > HISTORY) a.shift();
  }
  const visible = state.devices[active] === dev && document.visibilityState === 'visible';
  for (const w of dev.widgets) {
    const inst = page.widgets.get(w.id);
    if (!inst) continue;
    const max = inst.maxFrom ? inst.maxFrom.reduce((sum, id) => {
      const x = conn.value(frame, id);
      return sum == null || x == null ? null : sum + x;
    }, 0) : null;
    inst.update(conn.value(frame, w.sensor), visible, max, w.extra?.map((x) => conn.value(frame, x.sensor)));
  }
  if (dlgWidget.open && editTarget?.dev === dev) { takeFrame(conn, frame); paintLive(); }
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

// ---------- dashboard layout: saved on each PC, cached in this browser

/**
 * Saves a changed layout here right away and on the PC shortly after (edits come in bursts while dragging).
 * `adopt: false` when the phone layout was just dropped on purpose (else a phone edit makes it the phone's own).
 */
function saveLayout(dev, { adopt = true } = {}) {
  if (adopt) adoptPhoneView(dev);
  dev.layoutDirty = true;
  persist();
  clearTimeout(dev._pushTimer);
  dev._pushTimer = setTimeout(() => pushLayout(dev), 400);
}

async function pushLayout(dev) {
  const conn = conns.get(dev.key);
  if (!conn || conn.status !== 'online' || dev._pushing) return;
  dev._pushing = true;
  const sent = JSON.stringify(dev.layouts);
  try {
    const rev = await conn.saveDashboard(dev.layouts);
    dev.layoutRev = rev;
    if (JSON.stringify(dev.layouts) === sent) dev.layoutDirty = false;   // else another edit came in meanwhile
    persist();
  } catch { /* offline: stays dirty and is sent on reconnect */ }
  finally {
    dev._pushing = false;
    if (dev.layoutDirty) { clearTimeout(dev._pushTimer); dev._pushTimer = setTimeout(() => pushLayout(dev), 1000); }
  }
}

/** The PC announced its layout revision (on connect and after every save from any browser). */
async function syncLayout(dev, rev) {
  if (dev.layoutDirty) { pushLayout(dev); return; }     // changes made here (perhaps offline) win
  if (dev._pushing || rev === dev.layoutRev) return;
  const conn = conns.get(dev.key);
  let saved;
  try { saved = await conn.loadDashboard(); } catch { return; }   // offline or signed out; the next announcement retries
  if (!saved.rev || !Array.isArray(saved.widgets)) {
    // Nothing on the PC yet: this browser's layouts become the PC's.
    if (dev.layouts.desktop.length || dev.layouts.phone) { dev.layoutDirty = true; pushLayout(dev); }
    return;
  }
  if (dev.layoutDirty || saved.rev === dev.layoutRev) return;
  dev.layouts = normalizeLayouts(saved.widgets, saved.phone);
  dev.layoutRev = saved.rev;
  persist();
  const p = pages.get(dev.key);
  if (p?.dragging) p.pending = true; else renderPage(dev);
  conn.subscribe();
}

// ---------- rendering
function renderPages() {
  const host = $('#pages');
  const keep = new Set(state.devices.map((d) => d.key));
  for (const [key, p] of pages) {
    if (!keep.has(key)) { p.widgets.forEach((w) => w.destroy()); p.ro.disconnect(); p.section.remove(); pages.delete(key); }
  }
  state.devices.forEach((dev) => {
    let p = pages.get(dev.key);
    if (!p) {
      const section = document.createElement('section');
      section.className = 'page';
      const msg = document.createElement('div');
      msg.className = 'page-msg';
      const board = document.createElement('div');
      board.className = 'board';
      section.append(msg, board);
      p = { section, msg, board, widgets: new Map(), series: new Map(), cols: 0 };
      // Columns follow the page width: 4 on a phone, up to 16 on a wide screen.
      p.ro = new ResizeObserver(() => applyColumns(p, dev));
      p.ro.observe(board);
      enableDnd(board, section, {
        maxRows: ROW_LIMIT,
        onReorder: () => reorderFromBoard(dev),
        onResize: (id, w, h) => {
          const item = dev.widgets.find((x) => x.id === id);
          if (!item) return;
          item.w = w; item.h = h;
          saveLayout(dev);
          applyColumns(p, dev, true);
        },
      });
      section.addEventListener('pointerdown', () => { p.dragging = document.body.classList.contains('editing'); }, true);
      addEventListener('pointerup', () => {
        if (!p.dragging) return;
        p.dragging = false;
        if (p.pending) { p.pending = false; renderPage(dev); }
      });
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

const columnsFor = (width) => Math.max(4, Math.min(MAX_W, Math.floor((width + 10) / 90)));

/** Sets the grid's column count and each card's span (a card never spans more columns than there are). */
function applyColumns(p, dev, force = false) {
  const cols = columnsFor(p.board.clientWidth);
  if (cols === p.cols && !force) return;
  p.cols = cols;
  p.board.style.setProperty('--cols', cols);
  for (const item of dev.widgets) {
    const el = p.widgets.get(item.id)?.el;
    if (!el) continue;
    el.dataset.w = item.w;
    el.dataset.h = item.h;
    el.style.gridColumnEnd = `span ${Math.min(item.w, cols)}`;
    el.style.gridRowEnd = `span ${item.h}`;
  }
}

/** Splits the flat item list into sections: [{ section | null, items }]. Items before the first section come first. */
function groupsOf(items) {
  const groups = [{ section: null, items: [] }];
  for (const x of items) {
    if (isSection(x)) groups.push({ section: x, items: [] });
    else groups[groups.length - 1].items.push(x);
  }
  return groups;
}

function renderPage(dev) {
  const p = pages.get(dev.key);
  if (!p) return;
  const conn = connectionFor(dev);
  p.widgets.forEach((w) => w.destroy());
  p.widgets.clear();
  p.board.replaceChildren();

  for (const g of groupsOf(dev.widgets)) {
    const group = document.createElement('div');
    group.className = 'group' + (g.section ? '' : ' untitled');
    if (g.section) {
      group.dataset.section = g.section.id;
      group.classList.toggle('collapsed', !!state.collapsed[g.section.id]);
      group.append(sectionHead(dev, g.section, group));
    }
    const grid = document.createElement('div');
    grid.className = 'grid';
    for (const w of g.items) {
      const sensor = conn.sensors.get(w.sensor);
      const inst = createWidget(w, sensor, widgetTools(dev, w), (id) => conn.sensors.get(id), (id) => {
        let a = p.series.get(id);
        if (!a) p.series.set(id, (a = []));
        return a;
      });
      inst.maxFrom = effectiveMaxFrom(w, sensor, conn.catalog);
      inst.signature = widgetSignature(w, conn);
      inst.el.addEventListener('click', (e) => {
        if (!editing && !e.target.closest('button')) openHistory(dev, w.id);
      });
      inst.el.tabIndex = 0;
      inst.el.title = 'Show history';
      inst.el.addEventListener('keydown', (e) => { if (!editing && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openHistory(dev, w.id); } });
      p.widgets.set(w.id, inst);
      grid.append(inst.el);
    }
    const add = document.createElement('button');
    add.className = 'add-tile';
    add.textContent = '+ Add widget';
    add.onclick = () => openWidgetEditor(dev, null, g.section?.id ?? null);
    grid.append(add);
    group.append(grid);
    p.board.append(group);
  }

  // Edit mode: page-level actions.
  const tools = document.createElement('div');
  tools.className = 'board-tools';
  const addSection = document.createElement('button');
  addSection.className = 'ghost';
  addSection.textContent = '+ Add section';
  addSection.onclick = () => {
    const sec = { id: uid(), kind: 'section', title: 'New section' };
    dev.widgets.push(sec);
    saveLayout(dev);
    renderPage(dev);
    const input = p.board.querySelector(`.group[data-section="${sec.id}"] .sec-title-input`);
    input?.focus(); input?.select();
    input?.scrollIntoView({ block: 'nearest' });
  };
  tools.append(addSection);
  // Which of the two layouts this is, and on phones a way back to the desktop layout.
  const which = document.createElement('p');
  which.className = 'layout-which';
  if (layoutMode() === 'desktop') {
    which.innerHTML = '<strong>Desktop layout.</strong> Phones have their own; the phone button above edits it.';
  } else if (!dev.layouts.phone) {
    which.innerHTML = '<strong>Phone layout.</strong> Same as the desktop layout until you change something here.';
  } else {
    which.innerHTML = '<strong>Phone layout.</strong> Separate from the desktop layout.';
    const same = document.createElement('button');
    same.className = 'ghost';
    same.textContent = '↺ Use the desktop layout';
    same.onclick = () => {
      if (!confirm(`Drop the phone layout of ${dev.name} and show the desktop layout on phones again?`)) return;
      dev.layouts.phone = null;
      saveLayout(dev, { adopt: false }); renderPage(dev); conn.subscribe();
    };
    tools.append(same);
  }
  tools.prepend(which);
  if (dev.widgets.length) {
    const reset = document.createElement('button');
    reset.className = 'ghost';
    reset.textContent = '↺ Suggested layout';
    reset.onclick = () => {
      if (!conn.catalog.length || !confirm(`Replace the ${dev.name} dashboard with the suggested layout?`)) return;
      dev.widgets = suggest(conn.catalog).map((w) => ({ ...w, id: uid() }));
      saveLayout(dev); renderPage(dev); conn.subscribe();
    };
    tools.append(reset);
  }
  p.board.append(tools);
  applyColumns(p, dev, true);
  renderMessage(dev);
}

function sectionHead(dev, sec, group) {
  const head = document.createElement('div');
  head.className = 'sec-head';
  head.innerHTML = `
    <button class="grip edit-only" type="button" title="Drag to move this section" aria-label="Move section">${ICON.grip}</button>
    <button class="sec-toggle view-only" type="button" aria-expanded="true"><span class="sec-title"></span>${ICON.chevron}</button>
    <input class="sec-title-input edit-only" maxlength="40" aria-label="Section name">
    <button class="sec-del edit-only" type="button" title="Remove section (its widgets stay)">${ICON.del}</button>`;
  $('.sec-title', head).textContent = sec.title;
  const input = $('.sec-title-input', head);
  input.value = sec.title;
  input.onchange = () => {
    sec.title = input.value.trim() || 'Section';
    input.value = sec.title;
    $('.sec-title', head).textContent = sec.title;
    saveLayout(dev);
  };
  input.onkeydown = (e) => { if (e.key === 'Enter') input.blur(); };
  const toggle = $('.sec-toggle', head);
  toggle.setAttribute('aria-expanded', String(!state.collapsed[sec.id]));
  toggle.onclick = () => {
    const collapsed = !group.classList.contains('collapsed');
    group.classList.toggle('collapsed', collapsed);
    toggle.setAttribute('aria-expanded', String(!collapsed));
    if (collapsed) state.collapsed[sec.id] = true; else delete state.collapsed[sec.id];
    persist();
    if (!collapsed) refreshVisible();
  };
  $('.sec-del', head).onclick = () => {
    dev.widgets = dev.widgets.filter((x) => x !== sec);
    delete state.collapsed[sec.id];
    saveLayout(dev); renderPage(dev);
  };
  return head;
}

/** After a drag: the new order is whatever the board now shows. */
function reorderFromBoard(dev) {
  const p = pages.get(dev.key);
  const byId = new Map(dev.widgets.map((x) => [x.id, x]));
  const next = [];
  for (const group of p.board.querySelectorAll(':scope > .group')) {
    if (group.dataset.section) next.push(byId.get(group.dataset.section));
    for (const card of group.querySelectorAll(':scope > .grid > .card')) next.push(byId.get(card.dataset.id));
  }
  dev.widgets = next.filter(Boolean);
  saveLayout(dev);
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
  const widgets = dev.widgets.filter((x) => !isSection(x));
  if (!p || !conn || p.widgets.size !== widgets.length) return false;
  for (const w of widgets) {
    const inst = p.widgets.get(w.id);
    if (!inst || inst.signature !== widgetSignature(w, conn)) return false;
  }
  for (const w of widgets) p.widgets.get(w.id).setSensor(conn.sensors.get(w.sensor), (id) => conn.sensors.get(id));
  renderMessage(dev);
  return true;
}

/** Edit mode controls on a card: drag handle, edit, remove, and the resize corner. */
function widgetTools(dev, w) {
  const tools = document.createElement('div');
  tools.className = 'card-tools';
  tools.innerHTML = `
    <div class="tool-row">
      <button class="grip" type="button" title="Drag to move" aria-label="Move">${ICON.grip}</button>
      <button class="edit" type="button" title="Edit" aria-label="Edit">${ICON.edit}</button>
      <button class="del" type="button" title="Remove" aria-label="Remove">${ICON.del}</button>
    </div>
    <span class="resize" title="Drag to resize" aria-hidden="true">${ICON.resize}</span>`;
  $('.edit', tools).onclick = () => openWidgetEditor(dev, w);
  $('.del', tools).onclick = () => {
    dev.widgets = dev.widgets.filter((x) => x !== w);
    saveLayout(dev); renderPage(dev); conns.get(dev.key)?.subscribe();
  };
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
    p.board.hidden = true;
    return;
  }
  p.board.hidden = false;

  if (conn?.status === 'offline') {
    p.msg.innerHTML = `<div class="notice"><strong>Can't reach ${escapeHtml(dev.name)}.</strong> Retrying… <span class="sub">${escapeHtml(dev.url)}</span></div>`;
  } else if (!dev.widgets.length && conn?.status === 'online') {
    const box = document.createElement('div');
    box.className = 'empty';
    box.innerHTML = '<p><strong>This dashboard is empty.</strong></p><p>Start with a suggested layout or add widgets one by one.</p><div class="inline"><button class="primary" data-a="quick">Suggested layout</button><button class="ghost" data-a="add">Add widget</button></div>';
    $('[data-a="quick"]', box).onclick = () => {
      dev.widgets = suggest(conn.catalog).map((w) => ({ ...w, id: uid() }));
      saveLayout(dev); renderPage(dev); conn.subscribe();
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

function refreshVisible() {
  const dev = state.devices[active];
  if (dev) pages.get(dev.key)?.widgets.forEach((w) => w.refresh());
}

// ---------- edit mode
$('#btn-edit').onclick = () => {
  editing = !editing;
  document.body.classList.toggle('editing', editing);
  $('#btn-edit').setAttribute('aria-pressed', String(editing));
};

// ---------- phone preview: the dashboard at phone width (4 columns), to lay it out for the people who use it there
function applyPreview() {
  document.body.classList.toggle('phone-preview', state.preview);
  $('#btn-preview').setAttribute('aria-pressed', String(state.preview));
  updateLayoutMode();
  requestAnimationFrame(() => { $('#pages').scrollTo({ left: active * $('#pages').clientWidth }); refreshVisible(); });
}

// Phones (narrow screens) and the phone preview show each PC's phone layout; wider screens its desktop layout.
const PHONE_WIDTH = 700;
function wantedLayoutMode() { return state.preview || innerWidth < PHONE_WIDTH ? 'phone' : 'desktop'; }
function updateLayoutMode() {
  const next = wantedLayoutMode();
  if (next === layoutMode()) return;
  setLayoutMode(next);
  for (const dev of state.devices) {
    if (!pages.has(dev.key)) continue;
    renderPage(dev);
    conns.get(dev.key)?.subscribe();
  }
}
addEventListener('resize', updateLayoutMode);
$('#btn-preview').onclick = () => {
  state.preview = !state.preview;
  persist();
  applyPreview();
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
// How full memory or a drive is (a percentage of "used") sits with the sizes, not with processor load.
const isFullness = (s) => s.unit === '%' && /\bused\b|\bspace\b/i.test(s.name);
// NVIDIA cards also report power as a share of their power limit ("GPU Power", "GPU Board Power" in %): that's power too.
const isPowerShare = (s) => s.type === 'Load' && /\bpower\b/i.test(s.name);
const readingCat = (s) => (isFullness(s) ? 'data' : isPowerShare(s) ? 'power'
  : READING_CATS.find(([, , types]) => types.includes(s.type))?.[0] || 'other');
const typeLabel = (s) => (isFullness(s) ? 'Usage' : isPowerShare(s) ? '% of power limit' : TYPE_LABEL[s.type] || s.type);
const pick = { dev: 'all', read: 'all' };
let unitShown = null;   // the size unit the form's numbers are in

function openWidgetEditor(dev, widget, sectionId = null) {
  const conn = conns.get(dev.key);
  if (!conn?.catalog.length) { alert('Connect to this device first to see its sensors.'); return; }
  editTarget = { dev, widget, sectionId };
  extrasShown = [];
  extrasTouched = false;
  $('#w-extra-picker').hidden = true;
  $('#widget-title').textContent = widget ? 'Edit widget' : 'Add widget';
  $('#w-search').value = '';
  const current = widget && conn.sensors.get(widget.sensor);
  if (current) { pick.dev = deviceCat(current); pick.read = readingCat(current); }
  else { pick.dev = DEVICE_CATS.find(([, , test]) => conn.catalog.some(test))?.[0] || 'other'; pick.read = 'all'; }
  // Every sensor's live reading while the editor is open: start from the dashboard's latest, then stream them all.
  liveValues.clear();
  takeFrame(conn, conn.last);
  conn.setWatchAll(true);
  fillSensorList(conn.catalog, widget?.sensor);
  const sensor = conn.sensors.get($('#w-sensor').value);
  fillWidgetForm(widget || defaultsFor(sensor || conn.catalog[0], conn.catalog), sensor, conn.catalog);
  dlgWidget.showModal();
  showActiveTab();
  revealSensor();
}

// ---------- live readings in the editor
const liveValues = new Map();   // sensor id → latest value, while the editor is open
function takeFrame(conn, frame) {
  if (!frame) return;
  for (const [id, i] of conn.index) liveValues.set(id, frame.v[i]);
}
function paintLive() {
  const conn = conns.get(editTarget?.dev.key);
  if (!conn) return;
  const put = (el, id, unit) => {
    const s = conn.sensors.get(id);
    const text = readingText(s, liveValues.get(id), unit || s?.unit);
    if (el.textContent !== text) el.textContent = text;
  };
  for (const row of dlgWidget.querySelectorAll('.sensor-list .opt')) put(row.lastElementChild, row.dataset.id);
  for (const li of $('#w-extra-list').children) {
    const x = extrasShown.find((e) => e.sensor === li.dataset.id);
    put(li.querySelector('.v'), li.dataset.id, x && displayUnit(x, conn.sensors.get(x.sensor)));
  }
}

// ---------- sensor list: a listbox, so every row can show the sensor's live reading
const sensorList = $('#w-sensor');
let sensorPicked = '';
Object.defineProperty(sensorList, 'value', { get: () => sensorPicked, set: (id) => markSensor(id) });

function markSensor(id) {
  sensorPicked = '';
  sensorList.removeAttribute('aria-activedescendant');
  for (const row of sensorList.querySelectorAll('.opt')) {
    const on = row.dataset.id === id;
    row.setAttribute('aria-selected', String(on));
    if (on) { sensorPicked = id; sensorList.setAttribute('aria-activedescendant', row.id); }
  }
}

/** Scrolls the list so the chosen sensor is visible below its device's sticky name. */
function revealSensor() {
  const row = sensorList.querySelector('.opt[aria-selected="true"]');
  if (!row) return;
  const head = row.parentElement.querySelector('.grp-name')?.offsetHeight || 0;
  const top = row.offsetTop - head;
  const bottom = row.offsetTop + row.offsetHeight;
  if (top < sensorList.scrollTop) sensorList.scrollTop = top;
  else if (bottom > sensorList.scrollTop + sensorList.clientHeight) sensorList.scrollTop = bottom - sensorList.clientHeight;
}

function chooseSensor(row) {
  if (!row) return;
  if (row.dataset.id !== sensorPicked) {
    markSensor(row.dataset.id);
    sensorList.dispatchEvent(new Event('change'));
  }
  revealSensor();
}
sensorList.addEventListener('click', (e) => chooseSensor(e.target.closest('.opt')));
sensorList.addEventListener('keydown', (e) => {
  const rows = [...sensorList.querySelectorAll('.opt')];
  if (!rows.length) return;
  const i = rows.findIndex((r) => r.dataset.id === sensorPicked);
  const page = Math.max(1, Math.floor(sensorList.clientHeight / (rows[0].offsetHeight || 28)) - 1);
  const to = { ArrowDown: i + 1, ArrowUp: i - 1, Home: 0, End: rows.length - 1, PageDown: i + page, PageUp: i - page }[e.key];
  if (to === undefined) return;
  e.preventDefault();
  chooseSensor(rows[Math.max(0, Math.min(rows.length - 1, to))]);
});
// From the search box, the arrow keys and Enter go to the list (Enter would otherwise close the dialog).
$('#w-search').addEventListener('keydown', (e) => {
  if (e.key !== 'ArrowDown' && e.key !== 'Enter') return;
  e.preventDefault();
  sensorList.focus();
});

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
  if (tabs && host.id === 'w-dev') showActiveTab();
  else if (tabs) host.querySelector('.active')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
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

  renderSensorRows(sel, list, 'w-opt');
  markSensor(list.some((s) => s.id === selectedId) ? selectedId : list[0]?.id || '');
  revealSensor();
}

/** Sensor rows grouped by device, each with its live reading. `first` puts one device on top, with a note. */
function renderSensorRows(host, list, idPrefix, { first = null, firstNote = '' } = {}) {
  host.replaceChildren();
  host.scrollTop = 0;
  const groups = new Map(first ? [[first, []]] : []);
  for (const s of list) {
    if (!groups.has(s.hardware)) groups.set(s.hardware, []);
    groups.get(s.hardware).push(s);
  }
  let n = 0;
  for (const [hw, items] of groups) {
    if (!items.length) continue;
    const g = document.createElement('div');
    g.className = 'grp';
    g.setAttribute('role', 'group');
    g.setAttribute('aria-label', hw);
    const name = document.createElement('div');
    name.className = 'grp-name';
    name.setAttribute('aria-hidden', 'true');
    name.textContent = hw === first && firstNote ? `${hw} (${firstNote})` : hw;
    g.append(name);
    for (const s of items) {
      const o = document.createElement('div');
      o.className = 'opt';
      o.id = `${idPrefix}-${n++}`;
      o.dataset.id = s.id;
      o.setAttribute('role', 'option');
      o.setAttribute('aria-selected', 'false');
      o.innerHTML = `<span class="n">${escapeHtml(s.name)}<small>${escapeHtml(typeLabel(s))}</small></span>`
        + `<span class="v">${escapeHtml(readingText(s, liveValues.get(s.id)))}</span>`;
      g.append(o);
    }
    host.append(g);
  }
  if (!list.length) host.innerHTML = '<p class="empty">No sensor matches that.</p>';
}

function fillWidgetForm(w, sensor, catalog) {
  $('#w-type').value = w.type;
  const [dw, dh] = defaultSize(w.type);
  $('#w-w').value = w.w || dw;
  $('#w-h').value = w.h || dh;
  sizeType = w.type;
  fillStyleOptions(w.type, w.style, w.pos);
  $('#w-thick').value = String(Math.round(thicknessOf(w) * 100));
  $('#w-nsize').value = NUMBER_SIZE.some(([k]) => k === w.numSize) ? w.numSize : 'm';
  $('#w-upos').value = unitPosOf(w);
  syncThickness();
  extrasShown = extrasOf(w);
  renderExtras();
  $('#w-label').value = w.label || '';
  $('#w-label').placeholder = sensor?.name || '';
  $('#w-sub').value = w.sub || '';
  $('#w-sub').placeholder = sensor?.hardware || '';
  $('#w-sub-show').checked = !w.hideSub;
  syncSub();
  $('#w-min').value = w.min ?? '';
  $('#w-max').value = w.max ?? '';
  $('#w-dec').value = String(w.decimals ?? 0);
  const fallback = [COLORS.min, COLORS.med, COLORS.max];
  for (let i = 0; i < 3; i++) {
    const t = w.thresholds?.[i] || {};
    $(`#t${i}-v`).value = t.value ?? '';
    $(`#t${i}-c`).value = t.color || fallback[i];
  }

  // B … TB for sizes, Automatic or a fixed unit for data rates. The form's numbers are in the unit shown.
  const choices = unitChoices(sensor);
  $('#w-unit-field').hidden = !choices;
  unitShown = choices ? displayUnit(w, sensor) : null;
  $('#w-unit').replaceChildren(...(choices || []).map((u) => new Option(unitLabel(u), u)));
  if (choices) $('#w-unit').value = unitShown;

  // "Scale max follows the total": live total VRAM / RAM / drive size instead of a fixed number.
  const total = totalFor(sensor, catalog);
  $('#w-total-field').hidden = !total;
  $('#w-total').checked = !!total && !!effectiveMaxFrom(w, sensor, catalog);
  $('#w-total-hint').textContent = total ? `(${total.map((id) => conns.get(editTarget.dev.key)?.sensors.get(id)?.name).join(' + ')})` : '';
  syncTotal();
}

/** Gauge style and value position choices follow the display type. */
function fillStyleOptions(type, style, pos) {
  const styleSel = $('#w-style');
  if (!styleSel.options.length) for (const [v, l] of GAUGE_STYLES) styleSel.add(new Option(l, v));
  styleSel.value = GAUGE_STYLES.some(([v]) => v === style) ? style : 'arc';
  $('#w-style-field').hidden = type !== 'gauge';
  const choices = positionsFor(type);
  $('#w-pos-field').hidden = !choices;
  const posSel = $('#w-pos');
  posSel.replaceChildren();
  if (!choices) return;
  for (const [v, l] of choices) posSel.add(new Option(l, v));
  posSel.value = choices.some(([v]) => v === pos) ? pos : defaultPosition(type, styleSel.value);
}

let sizeType = null;   // the display type the size fields were last defaulted for
$('#w-type').addEventListener('change', () => {
  const type = $('#w-type').value;
  // A size still at the old type's default follows the new type (a gauge's 2×3 becomes a vertical bar's 1×3).
  const [ow, oh] = defaultSize(sizeType);
  if (Number($('#w-w').value) === ow && Number($('#w-h').value) === oh) [$('#w-w').value, $('#w-h').value] = defaultSize(type);
  sizeType = type;
  fillStyleOptions(type, $('#w-style').value, null);
  syncThickness();
});

for (const [k, l] of NUMBER_SIZE) $('#w-nsize').add(new Option(l, k));
for (const [k, l] of UNIT_POS) $('#w-upos').add(new Option(l, k));

/** Thickness applies to gauges, bars and graph lines; a big number has a number size and unit position instead. */
function syncThickness() {
  const type = $('#w-type').value;
  $('#w-thick-field').hidden = type === 'value';
  $('#w-num-field').hidden = type !== 'value';
  $('#w-thick-name').textContent = type === 'graph' ? 'Line thickness' : type === 'gauge' ? 'Arc thickness' : 'Bar thickness';
  const v = Number($('#w-thick').value);
  $('#w-thick-out').textContent = `${v}%${v === 100 ? ' (default)' : ''}`;
  $('#w-thick-reset').disabled = v === 100;
}
$('#w-thick').addEventListener('input', syncThickness);
$('#w-thick-reset').addEventListener('click', () => { $('#w-thick').value = '100'; syncThickness(); });

// ---------- more readings on the same card (up to MAX_EXTRA besides the main one)
let extrasShown = [];   // [{ sensor, show, align, size }] in the form
let extrasTouched = false;   // added, removed or changed by hand (kept when another main sensor is picked)
// The unit's place next to an extra reading's number, in short words for the small menu.
const EXTRA_UNIT_POS = UNIT_POS.map(([k]) => [k, { right: 'Right', left: 'Left', above: 'Above', below: 'Below', none: 'Hidden' }[k]]);

function renderExtras() {
  const conn = conns.get(editTarget?.dev.key);
  const list = $('#w-extra-list');
  list.replaceChildren();
  extrasShown = extrasShown.filter((x) => x.sensor !== sensorList.value);
  for (const x of extrasShown) {
    const s = conn?.sensors.get(x.sensor);
    const li = document.createElement('li');
    li.dataset.id = x.sensor;
    const choice = (key, label, options, current = x[key]) => `<label><span>${label}</span><select data-k="${key}">`
      + options.map(([k, l]) => `<option value="${k}"${k === current ? ' selected' : ''}>${l}</option>`).join('') + '</select></label>';
    const units = unitChoices(s);
    li.innerHTML = `<div class="x-head"><span class="n">${escapeHtml(s ? s.name : 'Sensor not available')}<small>${escapeHtml(s ? `${typeLabel(s)} · ${s.hardware}` : '')}</small></span>`
      + `<span class="v">${escapeHtml(readingText(s, liveValues.get(x.sensor), displayUnit(x, s)))}</span>`
      + `<button type="button" title="Remove from this card" aria-label="Remove">×</button></div>`
      + `<div class="x-opts"><label class="x-label"><span>Label</span><input type="text" data-k="label" maxlength="${MAX_EXTRA_LABEL}" placeholder="None" autocomplete="off"></label>`
      + `${choice('show', 'Show', EXTRA_SHOW)}${choice('align', 'Position', EXTRA_ALIGN)}${choice('size', 'Size', EXTRA_SIZE)}`
      + `${units ? choice('unit', 'Unit', units.map((u) => [u, unitLabel(u)]), displayUnit(x, s)) : ''}`
      + `${choice('unitPos', 'Suffix', EXTRA_UNIT_POS, unitPosOf(x))}</div>`;
    // The field shows what the card will show: the reading's own label, or the name it gets by default. Empty = none.
    const labelIn = li.querySelector('input[data-k="label"]');
    labelIn.value = extraLabel(x, s, conn?.sensors.get(sensorList.value));
    labelIn.oninput = () => { x.label = labelIn.value; extrasTouched = true; };
    // A graph without its number has no text to label, place or size.
    const syncOpts = () => li.querySelectorAll('[data-k]:not([data-k="show"])').forEach((c) => { c.disabled = x.show === 'graph'; });
    li.querySelectorAll('select').forEach((sel) => {
      sel.onchange = () => { x[sel.dataset.k] = sel.value; extrasTouched = true; syncOpts(); paintLive(); };
    });
    syncOpts();
    li.querySelector('button').onclick = () => { extrasShown = extrasShown.filter((y) => y !== x); extrasTouched = true; renderExtras(); };
    list.append(li);
  }
  list.hidden = !extrasShown.length;

  const add = $('#w-extra-add');
  const full = extrasShown.length >= MAX_EXTRA;
  add.textContent = full ? `${MAX_EXTRA + 1} sensors is the most a card shows` : '+ Add a reading';
  add.disabled = full || !conn;
  add.setAttribute('aria-expanded', String(!$('#w-extra-picker').hidden));
  if (add.disabled) showExtraPicker(false);
  else if (!$('#w-extra-picker').hidden) fillExtraPicker();
}

// The picker for an extra reading: device tabs (this card's device first) and a search, rows with live readings.
const extraPick = { dev: null, at: -1 };
function fillExtraPicker() {
  const conn = conns.get(editTarget?.dev.key);
  if (!conn) return;
  const q = $('#w-extra-search').value.trim().toLowerCase();
  const taken = new Set([sensorList.value, ...extrasShown.map((x) => x.sensor)]);
  const pool = conn.catalog.filter((s) => !taken.has(s.id));
  const main = conn.sensors.get(sensorList.value);
  const counts = new Map();
  for (const s of pool) counts.set(deviceCat(s), (counts.get(deviceCat(s)) || 0) + 1);
  if (!counts.get(extraPick.dev)) extraPick.dev = main && counts.get(deviceCat(main)) ? deviceCat(main) : [...counts.keys()][0];
  renderChoices($('#w-extra-dev'), DEVICE_CATS, extraPick.dev, counts, (k) => {
    extraPick.dev = k;
    $('#w-extra-search').value = '';
    fillExtraPicker();
  }, { tabs: true });
  $('#w-extra-picker').classList.toggle('searching', q.length > 0);
  const list = q
    ? pool.filter((s) => q.split(/\s+/).every((part) => `${s.hardware} ${s.name} ${s.type}`.toLowerCase().includes(part)))
    : pool.filter((s) => deviceCat(s) === extraPick.dev);
  renderSensorRows($('#w-extra-pick'), list, 'w-xopt', { first: main?.hardware, firstNote: "this card's device" });
  extraPick.at = -1;
}
function showExtraPicker(on) {
  $('#w-extra-picker').hidden = !on;
  $('#w-extra-add').setAttribute('aria-expanded', String(on));
  if (!on) return;
  $('#w-extra-search').value = '';
  extraPick.dev = null;
  fillExtraPicker();
  $('#w-extra-search').focus();
}
function addExtra(id) {
  if (!id || extrasShown.length >= MAX_EXTRA) return;
  const s = conns.get(editTarget?.dev.key)?.sensors.get(id);
  // Power, loads and data rates read well as a number with a small graph; a fan's RPM next to its % as a number.
  extrasShown.push({ sensor: id, show: s && ['Power', 'Throughput', 'Load'].includes(s.type) ? 'both' : 'value', align: 'center', size: 'm' });
  extrasTouched = true;
  showExtraPicker(false);
  renderExtras();
  $('#w-extra-add').focus();
}
$('#w-extra-add').addEventListener('click', () => showExtraPicker($('#w-extra-picker').hidden));
$('#w-extra-search').addEventListener('input', fillExtraPicker);
$('#w-extra-pick').addEventListener('click', (e) => addExtra(e.target.closest('.opt')?.dataset.id));
// Keyboard: arrows move through the rows (from the search box too), Enter adds, Escape closes the picker.
function extraKeys(e) {
  const rows = [...$('#w-extra-pick').querySelectorAll('.opt')];
  if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); showExtraPicker(false); $('#w-extra-add').focus(); return; }
  if (e.key === 'Enter') { e.preventDefault(); addExtra(rows[extraPick.at]?.dataset.id ?? (rows.length === 1 ? rows[0].dataset.id : null)); return; }
  const to = { ArrowDown: extraPick.at + 1, ArrowUp: extraPick.at - 1, Home: 0, End: rows.length - 1 }[e.key];
  if (to === undefined || !rows.length) return;
  e.preventDefault();
  extraPick.at = Math.max(0, Math.min(rows.length - 1, to));
  rows.forEach((r, i) => r.setAttribute('aria-selected', String(i === extraPick.at)));
  const row = rows[extraPick.at];
  $('#w-extra-pick').setAttribute('aria-activedescendant', row.id);
  row.scrollIntoView({ block: 'nearest' });
  if (document.activeElement !== $('#w-extra-search')) $('#w-extra-pick').focus();
}
$('#w-extra-search').addEventListener('keydown', extraKeys);
$('#w-extra-pick').addEventListener('keydown', extraKeys);
$('#w-style').addEventListener('change', () => {
  if ($('#w-type').value === 'gauge') $('#w-pos').value = defaultPosition('gauge', $('#w-style').value);
});

function syncSub() { $('#w-sub').disabled = !$('#w-sub-show').checked; }
$('#w-sub-show').addEventListener('change', syncSub);

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
  // Readings already added to the card stay.
  if (!editTarget.widget) {
    const d = defaultsFor(s, conn.catalog);
    if (extrasTouched) d.extra = extrasShown;
    fillWidgetForm(d, s, conn.catalog);
  } else fillWidgetForm(readForm(editTarget.widget.id), s, conn.catalog);
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

// Switching the unit converts the numbers already in the form.
$('#w-unit').addEventListener('change', () => {
  const next = $('#w-unit').value;
  const f = unitFactor(unitShown, next);
  for (const id of ['#w-min', '#w-max', '#t0-v', '#t1-v', '#t2-v']) {
    const v = $(id).value;
    if (v !== '') $(id).value = String(+(Number(v) * f).toPrecision(6));
  }
  $('#w-dec').value = String(decimalsFor(next));
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
    w: Math.max(1, Math.min(MAX_W, Math.round(Number($('#w-w').value) || 1))),
    h: Math.max(1, Math.min(MAX_H, Math.round(Number($('#w-h').value) || 1))),
    style: $('#w-type').value === 'gauge' ? $('#w-style').value : undefined,
    pos: positionsFor($('#w-type').value) ? $('#w-pos').value : undefined,
    thick: $('#w-type').value !== 'value' && Number($('#w-thick').value) !== 100 ? Number($('#w-thick').value) : undefined,
    numSize: $('#w-type').value === 'value' && $('#w-nsize').value !== 'm' ? $('#w-nsize').value : undefined,
    unitPos: $('#w-type').value === 'value' && $('#w-upos').value !== 'right' ? $('#w-upos').value : undefined,
    extra: extrasShown.length ? extrasShown.filter((x) => x.sensor !== $('#w-sensor').value).slice(0, MAX_EXTRA) : undefined,
    label: $('#w-label').value.trim(),
    sub: $('#w-sub').value.trim() || undefined,
    hideSub: !$('#w-sub-show').checked || undefined,
    min: num('#w-min') ?? 0,
    max: useTotal ? null : num('#w-max'),
    maxFrom: useTotal ? total : [],
    unit: unitChoices(sensor) && $('#w-unit').value !== sensor.unit ? $('#w-unit').value : undefined,
    decimals: Number($('#w-dec').value),
    thresholds: [0, 1, 2].map((i) => ({ value: num(`#t${i}-v`), color: $(`#t${i}-c`).value })),
  };
}

/** Where a new widget goes: the end of its section (or of the unsectioned part at the top). */
function insertIndex(items, sectionId) {
  let i = sectionId ? items.findIndex((x) => x.id === sectionId) : -1;
  if (sectionId && i < 0) return items.length;
  for (i += 1; i < items.length && !isSection(items[i]); i++);
  return i;
}

dlgWidget.addEventListener('close', () => {
  if (editTarget) conns.get(editTarget.dev.key)?.setWatchAll(false);
  if (dlgWidget.returnValue !== 'save' || !editTarget) return;
  const { dev, widget, sectionId } = editTarget;
  const next = readForm(widget?.id || uid());
  if (!next.sensor) return;
  const at = widget ? dev.widgets.findIndex((x) => x.id === widget.id) : -1;
  if (at >= 0) dev.widgets[at] = next;
  else dev.widgets.splice(insertIndex(dev.widgets, sectionId), 0, next);
  saveLayout(dev);
  renderPage(dev);
  conns.get(dev.key)?.subscribe();
  editTarget = null;
});

// ---------- settings
const dlgSettings = $('#dlg-settings');
$('#btn-settings').onclick = () => { renderSettings(); dlgSettings.showModal(); };
dlgSettings.querySelector('[data-close]').onclick = () => dlgSettings.close();
dlgSettings.addEventListener('click', (e) => { if (e.target === dlgSettings) dlgSettings.close(); });

// ---------- keep the screen on (Screen Wake Lock), by default on phones and tablets
// The browser lets go of the lock whenever the page is hidden, so it's asked for again each time it comes back.
let wakeLock = null;
let wakeAsking = false;
const touchFirst = matchMedia('(pointer: coarse)');
const wantAwake = () => state.awake === 'on' || (state.awake === 'auto' && touchFirst.matches);

async function syncWakeLock() {
  const want = wantAwake() && document.visibilityState === 'visible';
  if (want && !wakeLock && !wakeAsking && 'wakeLock' in navigator) {
    wakeAsking = true;
    try {
      const lock = await navigator.wakeLock.request('screen');
      lock.addEventListener('release', () => { if (wakeLock === lock) wakeLock = null; });
      wakeLock = lock;
      if (!wantAwake() || document.visibilityState !== 'visible') syncWakeLock();
    } catch { /* refused: battery saver, or no recent tap on some browsers (retried on the next one) */ }
    wakeAsking = false;
  } else if (!want && wakeLock) {
    const lock = wakeLock;
    wakeLock = null;
    lock.release().catch(() => {});
  }
}

function renderAwake() {
  document.querySelectorAll('#awake button').forEach((b) => b.classList.toggle('active', b.dataset.awake === state.awake));
  $('#awake-hint').textContent = 'wakeLock' in navigator
    ? 'While the dashboard is open and in front, the screen doesn\'t dim or lock.'
    : !window.isSecureContext
      ? 'Browsers only keep the screen on for secure pages: open the dashboard at its https:// address (see "Set up phone" in the tray app).'
      : 'This browser can\'t keep the screen on.';
}

$('#awake').addEventListener('click', (e) => {
  const v = e.target.closest('button')?.dataset.awake;
  if (!v) return;
  state.awake = v;
  persist();
  renderAwake();
  syncWakeLock();
});
document.addEventListener('visibilitychange', syncWakeLock);
// Some browsers only grant the lock right after a tap; asking again on the next one is free when it's held.
document.addEventListener('pointerdown', syncWakeLock, { passive: true });
touchFirst.addEventListener?.('change', syncWakeLock);

$('#theme').addEventListener('click', (e) => {
  const t = e.target.closest('button')?.dataset.theme;
  if (!t) return;
  state.theme = t;
  persist();
  applyTheme();
});

function renderSettings() {
  applyTheme();
  renderAwake();
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
      if (existing) {
        existing.layouts = normalizeLayouts(d.layouts?.desktop ?? d.widgets, d.layouts?.phone);
        existing.layoutDirty = true;   // goes to the PC on the next connection
        existing.interval = d.interval || existing.interval;
        existing.name = d.name || existing.name;
      } else state.devices.push(normalizeDevice({ ...d, key: uid(), token: null, layoutDirty: true }));
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

// ---------- history: tap a card for its sensor's last 1 to 30 days
const dlgHistory = $('#dlg-history');
const RANGES = [[1, 'Last 24 hours'], [3, 'Last 3 days'], [7, 'Last 7 days'], [14, 'Last 14 days'], [30, 'Last 30 days']];
const chart = new HistoryChart($('#h-canvas'), $('#h-tip'));
let historyOf = null;    // { dev, id }
let historyTimer = 0;
let historyRequest = 0;

function openHistory(dev, id) {
  historyOf = { dev, id };
  $('#h-range').value = '0';    // always opens on the last day
  dlgHistory.showModal();
  loadHistory();
}

async function loadHistory() {
  if (!historyOf) return;
  const { dev, id } = historyOf;
  const w = dev.widgets.find((x) => x.id === id);
  const conn = conns.get(dev.key);
  if (!w || !conn) { dlgHistory.close(); return; }
  const sensor = conn.sensors.get(w.sensor);
  const [days, rangeLabel] = RANGES[Number($('#h-range').value)] || RANGES[0];
  $('#h-title').textContent = w.label || sensor?.name || 'Sensor';
  $('#h-sub').textContent = [sensor?.hardware, sensor?.name !== (w.label || sensor?.name) ? sensor?.name : null].filter(Boolean).join(' · ');
  $('#h-range-label').textContent = rangeLabel;
  $('#h-range').setAttribute('aria-valuetext', rangeLabel);
  document.querySelectorAll('#h-ticks button').forEach((b) => b.classList.toggle('active', b.dataset.i === $('#h-range').value));
  clearTimeout(historyTimer);
  historyTimer = setTimeout(loadHistory, 60_000);   // keep the open chart current

  const req = ++historyRequest;
  $('#h-empty').hidden = true;
  try {
    const data = await conn.history(w.sensor, days);
    if (req !== historyRequest) return;
    const unit = displayUnit(w, sensor);
    chart.set(data, {
      unit, k: unitFactor(sensor?.unit, unit), decimals: w.decimals ?? 0,
      thresholds: w.thresholds, scaleMin: w.min, scaleMax: effectiveMaxFrom(w, sensor, conn.catalog) ? null : w.max,
    });
    const st = chart.stats();
    const f = (v) => { const o = formatValue(v, unit, w.decimals); return `<b>${o}</b>`; };
    $('#h-stats').innerHTML = st ? `<span>Lowest ${f(st.lo)}</span><span>Average ${f(st.avg)}</span><span>Highest ${f(st.hi)}</span>` : '';
    if (!st) {
      $('#h-empty').textContent = data.enabled === false
        ? 'History is turned off on this PC (HistoryEnabled in config.json).'
        : 'No history for this range yet. SYSCOM records every sensor while it runs, and the first point shows up within a minute.';
      $('#h-empty').hidden = false;
    }
  } catch (ex) {
    if (req !== historyRequest) return;
    chart.set({ step: 60, start: 0, avg: [], min: [], max: [] }, {});
    $('#h-stats').innerHTML = '';
    $('#h-empty').textContent = `Couldn't load the history: ${ex.message}`;
    $('#h-empty').hidden = false;
  }
}

function formatValue(v, unit, decimals) {
  const f = format(v, unit, decimals);
  return `${f.n}${f.u ? ' ' + f.u : ''}`;
}

$('#h-range').addEventListener('input', loadHistory);
$('#h-ticks').addEventListener('click', (e) => {
  const b = e.target.closest('button');
  if (!b) return;
  $('#h-range').value = b.dataset.i;
  loadHistory();
});
dlgHistory.querySelector('[data-close]').onclick = () => dlgHistory.close();
dlgHistory.addEventListener('click', (e) => { if (e.target === dlgHistory) dlgHistory.close(); });
dlgHistory.addEventListener('close', () => { clearTimeout(historyTimer); historyOf = null; });

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
syncWakeLock();
applyPreview();

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
