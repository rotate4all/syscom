// The API window: ready-to-run examples for a card, a section or the whole dashboard (with this PC's own address
// and ids), the full reference, and the PC's API keys. The reference itself lives in api-reference.js.
import {
  GROUPS, INTRO, STREAM_ABOUT, CATEGORIES, KINDS, ACCESS, KEY_PLACEHOLDER, endpoint, examples, streamExample, urlOf,
  categoryOf, kindOf,
} from './api-reference.js';

const $ = (s, root = document) => root.querySelector(s);
const DOCS = 'https://github.com/rotate4all/syscom/blob/main/docs/API.md';
const LANGS = [['curl', 'curl'], ['powershell', 'PowerShell'], ['python', 'Python'], ['javascript', 'JavaScript'], ['browser', 'Browser']];
const secrets = new Map();   // dev.key → { name, secret } of the key created on this page (shown once; examples use it)
const dlg = $('#dlg-api');
let view = null;             // { dev, conn, scope, tab, layout }
let lang = 'curl';
try { lang = localStorage.getItem('syscom.apiLang') || 'curl'; } catch { /* private mode */ }

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
/** `code` and **bold** in the reference texts. */
const md = (s) => esc(s).replace(/`([^`]+)`/g, '<code>$1</code>').replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>');

/**
 * Opens the API window for a PC. scope: { kind: 'widget' | 'section' | 'dashboard', item } (what it was opened
 * from); tab: 'this' | 'keys' | 'ref'; layout: 'phone' when the phone layout (a separate one) is on screen.
 */
export function openApi(dev, conn, { scope = { kind: 'dashboard' }, tab = 'this', layout } = {}) {
  view = { dev, conn, scope, tab, layout };
  $('#api-sub').textContent = dev.name;
  $('#api-title').textContent = scope.kind === 'widget' ? `API · ${scope.item.label || conn.sensors.get(scope.item.sensor)?.name || 'Card'}`
    : scope.kind === 'section' ? `API · ${scope.item.title || 'Section'}` : 'API';
  $('#api-tab-this').textContent = { widget: 'This card', section: 'This section', dashboard: 'This dashboard' }[scope.kind];
  render();
  if (!dlg.open) dlg.showModal();
}

function render() {
  for (const b of dlg.querySelectorAll('#api-tabs button')) b.classList.toggle('active', b.dataset.tab === view.tab);
  const body = $('#api-body');
  body.innerHTML = view.tab === 'keys' ? keysHtml() : view.tab === 'ref' ? referenceHtml() : scopeHtml();
  body.scrollTop = 0;
  if (view.tab === 'keys') loadKeys();
}

// ---------------------------------------------------------------- what the examples are filled in with

function context() {
  const { dev, conn, scope, layout } = view;
  const catalog = conn.catalog;
  const widgets = dev.widgets.filter((w) => w.kind !== 'section' && w.sensor);
  const own = scope.kind === 'widget' ? [scope.item] : scope.kind === 'section' ? sectionCards(scope.item) : widgets;
  const ids = [...new Set(own.flatMap((w) => [w.sensor, ...(w.extra || []).map((x) => x.sensor)]))].filter((id) => conn.sensors.has(id));
  const fallback = catalog.find((s) => s.hardwareType === 'Cpu' && s.type === 'Temperature') || catalog[0];
  const sample = conn.sensors.get(ids[0]) || fallback || { id: '/amdcpu/0/temperature/2', name: 'CPU', hardware: '', unit: '' };
  const widget = scope.kind === 'widget' ? scope.item : widgets[0];
  const section = scope.kind === 'section' ? scope.item : dev.widgets.find((x) => x.kind === 'section');
  return {
    base: new URL(dev.url).origin,
    key: secrets.get(dev.key)?.secret || KEY_PLACEHOLDER,
    pc: dev.name,
    layout,
    sensor: sample,
    sensors: (ids.length ? ids : catalog.slice(0, 2).map((s) => s.id)).slice(0, 8),
    category: categoryOf(sample),
    kind: kindOf(sample),
    widget: { id: widget?.id || 'CARD_ID', label: widget?.label || conn.sensors.get(widget?.sensor)?.name || 'Card' },
    section: { id: section?.id || 'SECTION_ID', title: section?.title || 'Section' },
  };
}

/** The cards of a section: the ones after it, up to the next section. */
function sectionCards(sec) {
  const items = view.dev.widgets;
  const at = items.indexOf(sec);
  const out = [];
  for (let i = at + 1; i < items.length && items[i].kind !== 'section'; i++) if (items[i].sensor) out.push(items[i]);
  return out;
}

// ---------------------------------------------------------------- "This card / section / dashboard"

function scopeHtml() {
  const ctx = context();
  const { scope } = view;
  const local = /^(localhost|127\.|\[::1\])/.test(new URL(ctx.base).hostname);
  const parts = [keyLine(), local ? `<p class="hint api-warn">This page was opened as <code>${esc(new URL(ctx.base).host)}</code>: that address only works on this PC. From other devices, use the PC's network address (the tray app shows it).</p>` : ''];
  const s = ctx.sensor;
  if (scope.kind === 'widget') {
    parts.push(block('widget', ctx, { title: 'What this card shows', note: 'Its value as the card shows it (unit, text, colour level), its scale and its extra readings.' }));
    parts.push(block('sensor', ctx, { title: `Its sensor: ${s.name}`, note: `Just the sensor (${s.hardware}), in its own unit (${s.unit || 'no unit'}).` }));
    if (ctx.sensors.length > 1) parts.push(block('values', ctx, { title: 'All its readings at once', note: 'The main reading and the extra ones, as compact as it gets.' }));
    parts.push(block('history', ctx, { title: 'Its history', note: 'Up to 30 days: average, lowest and highest per step.' }));
  } else if (scope.kind === 'section') {
    parts.push(block('section', ctx, { title: 'Every card in this section', note: 'Each card as it shows it: label, value, text, colour level.' }));
    if (ctx.sensors.length) parts.push(block('values', ctx, { title: 'Just the values', note: 'Every reading the section shows, by sensor id.' }));
  } else {
    parts.push(block('dashboard', ctx, { title: 'The whole dashboard', note: 'Every card in every section, with what it shows right now.' }));
    parts.push(block('sensors', ctx, { title: `Sensors by category: ${CATEGORIES.find(([id]) => id === ctx.category)?.[1] || ctx.category}, ${KINDS.find(([id]) => id === ctx.kind)?.[1] || ctx.kind}`, note: 'Any category and kind: see the Reference tab for the list, with what this PC has.' }));
    parts.push(block('categories', ctx, { title: 'What this PC has', note: 'Categories, hardware and kinds of readings, with counts.' }));
  }
  parts.push(streamHtml(ctx, scope.kind === 'dashboard' ? 'Live, pushed every second' : 'Live, pushed every second (WebSocket)'));
  parts.push(`<p class="hint">Everything else is in the <button type="button" class="link" data-tab="ref">reference</button> and in <a href="${DOCS}" target="_blank" rel="noopener">docs/API.md</a> on GitHub.</p>`);
  return parts.join('');
}

function keyLine() {
  const mine = secrets.get(view.dev.key);
  return mine
    ? `<p class="api-keyline ok">The examples use your new key <b>${esc(mine.name)}</b>. It works until you revoke it; copy it somewhere safe, it isn't shown again after you reload.</p>`
    : `<p class="api-keyline">Replace <code>${KEY_PLACEHOLDER}</code> with an API key: <button type="button" class="link" data-tab="keys">create one</button> (it is then filled in here).</p>`;
}

function block(id, ctx, opts = {}) {
  const ep = endpoint(id);
  const ex = examples(ep, ctx);
  const call = ep.call(ctx);
  const url = urlOf(ctx, call);
  const path = url.slice(ctx.base.length);
  return `<section class="api-ep">
    <div class="api-ep-head"><h4>${esc(opts.title || ep.summary)}</h4><span class="api-badge">${ACCESS[ep.access]}</span></div>
    ${opts.note ? `<p class="hint">${md(opts.note)}</p>` : ep.about ? `<p class="hint">${md(ep.about)}</p>` : ''}
    ${opts.params !== false && ep.params?.length && !opts.note ? `<table class="api-params">${ep.params.map(([k, v]) => `<tr><td><code>${esc(k)}</code></td><td>${md(v)}</td></tr>`).join('')}</table>` : ''}
    <div class="api-url"><code><b>${ep.method}</b> ${esc(url)}</code><button type="button" class="ghost small" data-copy="${esc(url)}">Copy</button></div>
    ${codeTabs(ex)}
    ${ep.method === 'GET' && !ep.noTry && !path.includes('CARD_ID') && !path.includes('SECTION_ID') ? `<div class="api-try"><button type="button" class="ghost small" data-try="${esc(path)}">Try it</button><pre class="api-out" hidden></pre></div>` : ''}
  </section>`;
}

function codeTabs(ex) {
  const langs = LANGS.filter(([k]) => ex[k]);
  const current = langs.some(([k]) => k === lang) ? lang : langs[0][0];
  return `<div class="api-code">
    <div class="api-langs">${langs.map(([k, n]) => `<button type="button" data-lang="${k}" class="${k === current ? 'active' : ''}">${n}</button>`).join('')}</div>
    ${langs.map(([k]) => `<div class="api-snippet" data-lang="${k}"${k === current ? '' : ' hidden'}><pre><code>${esc(ex[k])}</code></pre><button type="button" class="ghost small" data-copy-code>Copy</button></div>`).join('')}
  </div>`;
}

function streamHtml(ctx, title) {
  const ex = streamExample(ctx);
  return `<section class="api-ep">
    <div class="api-ep-head"><h4>${esc(title)}</h4><span class="api-badge">${ACCESS.read}</span></div>
    <p class="hint">${md(STREAM_ABOUT[0])}</p>
    ${codeTabs({ javascript: ex.javascript, python: ex.python })}
  </section>`;
}

// ---------------------------------------------------------------- reference

function referenceHtml() {
  const ctx = context();
  const counts = new Map();
  for (const s of view.conn.catalog) {
    const k = `${categoryOf(s)}|${kindOf(s)}`;
    counts.set(k, (counts.get(k) || 0) + 1);
    counts.set(categoryOf(s), (counts.get(categoryOf(s)) || 0) + 1);
  }
  const out = [keyLine()];
  for (const [title, paras] of INTRO) out.push(`<h3>${esc(title)}</h3>`, ...paras.map((p) => `<p>${md(p)}</p>`));
  for (const g of GROUPS) {
    out.push(`<h3>${esc(g.title)}</h3><p>${md(g.about)}</p>`);
    for (const ep of g.endpoints) out.push(block(ep.id, ctx, { params: true }));
  }
  out.push('<h3>Live readings (WebSocket)</h3>', ...STREAM_ABOUT.map((p) => `<p>${md(p)}</p>`), streamHtml(ctx, 'Example'));
  out.push('<h3>Categories and kinds on this PC</h3>', '<p>Filter <code>/api/sensors</code> and <code>/api/values</code> with <code>?category=</code> and <code>?kind=</code>. Click one for its address.</p>');
  out.push('<table class="api-cats"><thead><tr><th>category</th>', ...KINDS.map(([id]) => `<th>${id}</th>`), '</tr></thead><tbody>');
  for (const [cat, name] of CATEGORIES) {
    if (!counts.get(cat)) continue;
    out.push(`<tr><th><button type="button" class="link" data-filter="category=${cat}" title="${esc(name)}">${cat}</button> <small>${counts.get(cat)}</small></th>`,
      ...KINDS.map(([kind]) => {
        const n = counts.get(`${cat}|${kind}`);
        return `<td>${n ? `<button type="button" class="link" data-filter="category=${cat}&kind=${kind}">${n}</button>` : ''}</td>`;
      }), '</tr>');
  }
  out.push('</tbody></table><div class="api-url" id="api-filter-url" hidden><code></code><button type="button" class="ghost small" data-copy>Copy</button></div>');
  out.push(`<p class="hint">The same reference, with example values, is in <a href="${DOCS}" target="_blank" rel="noopener">docs/API.md</a> on GitHub.</p>`);
  return out.join('');
}

// ---------------------------------------------------------------- keys

function keysHtml() {
  const mine = secrets.get(view.dev.key);
  return `
    <p>API keys for <b>${esc(view.dev.name)}</b>. A <b>read</b> key can read every sensor and card; a <b>full</b> key can also change the dashboard and the device list. Each PC has its own keys.</p>
    <ul class="list api-keys" id="api-keys"><li class="hint">Loading…</li></ul>
    <form class="inline" id="api-new-key">
      <input name="name" maxlength="60" placeholder="What it's for, e.g. Home Assistant" aria-label="Key name">
      <select name="access" aria-label="Access"><option value="read">Read only</option><option value="full">Full access</option></select>
      <button class="primary">Create key</button>
    </form>
    <p class="error" id="api-key-error" hidden></p>
    <div class="api-secret"${mine ? '' : ' hidden'} id="api-secret">
      <p><b>Your new key${mine ? `, ${esc(mine.name)}` : ''}.</b> Copy it now: it isn't shown again (only a fingerprint of it is kept on the PC).</p>
      <div class="api-url"><code>${esc(mine?.secret || '')}</code><button type="button" class="ghost small" data-copy="${esc(mine?.secret || '')}">Copy</button></div>
      <p class="hint">The examples in this window use it until you reload the page. <button type="button" class="link" data-tab="this">See them</button></p>
    </div>`;
}

async function loadKeys() {
  const host = $('#api-keys');
  try {
    const keys = await view.conn.api('/api/keys');
    host.innerHTML = keys.length ? keys.map((k) => `<li>
      <span class="grow"><b>${esc(k.name)}</b> <span class="api-badge">${k.access === 'full' ? 'full access' : 'read only'}</span>
        <span class="sub"><code>${esc(k.prefix)}…</code> · created ${when(k.created)} · ${k.lastUsed ? `last used ${when(k.lastUsed)}` : 'not used yet'}</span></span>
      <button type="button" class="danger" data-revoke="${esc(k.id)}" data-name="${esc(k.name)}">Revoke</button></li>`).join('')
      : '<li class="hint">No keys yet.</li>';
  } catch (ex) {
    host.innerHTML = `<li class="error">${esc(ex.message === 'HTTP 404' ? 'This PC runs an older SYSCOM without the API: update it from its tray app.' : ex.message)}</li>`;
  }
}

function when(ms) {
  const d = new Date(ms);
  const days = Math.floor((Date.now() - ms) / 86_400_000);
  return days < 1 ? `today ${d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : days < 2 ? 'yesterday' : d.toLocaleDateString();
}

// ---------------------------------------------------------------- events

dlg.addEventListener('click', async (e) => {
  const t = e.target.closest('button, a');
  if (!t || !view) return;
  if (t.matches('[data-close]')) { dlg.close(); return; }
  if (t.dataset.tab) { view.tab = t.dataset.tab; render(); return; }
  if (t.dataset.lang) {
    lang = t.dataset.lang;
    try { localStorage.setItem('syscom.apiLang', lang); } catch { /* private mode */ }
    for (const box of dlg.querySelectorAll('.api-code')) {
      const has = box.querySelector(`.api-snippet[data-lang="${lang}"]`);
      if (!has && box !== t.closest('.api-code')) continue;
      for (const b of box.querySelectorAll('[data-lang]')) {
        if (b.matches('button')) b.classList.toggle('active', b.dataset.lang === lang);
        else b.hidden = b.dataset.lang !== lang;
      }
    }
    return;
  }
  if ('copy' in t.dataset) { copy(t.dataset.copy || t.previousElementSibling?.textContent || '', t); return; }
  if ('copyCode' in t.dataset) { copy(t.previousElementSibling.textContent, t); return; }
  if (t.dataset.try) { tryIt(t); return; }
  if (t.dataset.filter) {
    const box = $('#api-filter-url');
    const url = `${new URL(view.dev.url).origin}/api/sensors?${t.dataset.filter}`;
    box.hidden = false;
    $('code', box).textContent = url;
    $('button', box).dataset.copy = url;
    return;
  }
  if (t.dataset.revoke) {
    if (!confirm(`Revoke the key "${t.dataset.name}"? Anything using it stops working at once.`)) return;
    try { await view.conn.api(`/api/keys/${encodeURIComponent(t.dataset.revoke)}`, { method: 'DELETE' }); } catch (ex) { alert(ex.message); }
    loadKeys();
  }
});

dlg.addEventListener('submit', async (e) => {
  if (e.target.id !== 'api-new-key') return;
  e.preventDefault();
  const form = e.target;
  const err = $('#api-key-error');
  err.hidden = true;
  const name = form.name.value.trim() || 'API key';
  try {
    const { secret } = await view.conn.api('/api/keys', { method: 'POST', body: JSON.stringify({ name, access: form.access.value }) });
    secrets.set(view.dev.key, { name, secret });
    form.reset();
    render();
    $('#api-secret')?.scrollIntoView({ block: 'nearest' });
  } catch (ex) {
    err.textContent = ex.message;
    err.hidden = false;
  }
});

dlg.addEventListener('close', () => { view = null; });

async function tryIt(button) {
  const out = button.nextElementSibling;
  out.hidden = false;
  out.textContent = 'Asking the PC…';
  try {
    const data = await view.conn.api(button.dataset.try);
    const text = JSON.stringify(data, null, 2).split('\n');
    out.textContent = text.length > 80 ? [...text.slice(0, 80), `… ${text.length - 80} more lines`].join('\n') : text.join('\n');
  } catch (ex) {
    out.textContent = ex.message;
  }
}

/** Copies text; plain-HTTP pages have no clipboard API, so it falls back to a selection. */
async function copy(text, button) {
  let ok = false;
  try { await navigator.clipboard.writeText(text); ok = true; } catch {
    const area = document.createElement('textarea');
    area.value = text;
    area.style.position = 'fixed';
    area.style.opacity = '0';
    dlg.append(area);
    area.select();
    try { ok = document.execCommand('copy'); } catch { ok = false; }
    area.remove();
  }
  const old = button.textContent;
  button.textContent = ok ? 'Copied' : 'Select and copy';
  setTimeout(() => { button.textContent = old; }, 1500);
}
