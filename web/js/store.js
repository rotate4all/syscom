// Device list and this browser's preferences, in localStorage. Each PC's dashboard is saved on that PC (and cached
// here so it shows instantly and works offline).
import { normalizeItem } from './widgets.js';

const KEY = 'syscom.v1';

export const DEFAULT_INTERVAL = 1000;
export const MIN_INTERVAL = 500;

export function load() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { /* private mode or corrupt */ }
  return {
    theme: saved.theme || 'auto',
    active: saved.active || 0,
    preview: !!saved.preview,
    collapsed: saved.collapsed && typeof saved.collapsed === 'object' ? saved.collapsed : {},
    devices: Array.isArray(saved.devices) ? saved.devices.map(normalizeDevice) : [],
  };
}

export function save(state) {
  try { localStorage.setItem(KEY, JSON.stringify(state)); } catch { /* storage unavailable */ }
}

// ---- desktop and phone layouts
// Each PC keeps two independent layouts. dev.widgets is the one for this screen: the phone layout on phones and in
// phone preview, else the desktop one. Until the phone layout is changed on its own it follows the desktop layout;
// edits made on a phone then go to a copy, which becomes the phone layout when saved (adoptPhoneView).
let mode = 'desktop';
let phoneViews = new WeakMap();   // device → { from: desktop array, items: its copy }

export function layoutMode() { return mode; }
export function setLayoutMode(m) { mode = m; phoneViews = new WeakMap(); }

function phoneView(dev) {
  let v = phoneViews.get(dev);
  if (!v || v.from !== dev.layouts.desktop) {
    v = { from: dev.layouts.desktop, items: structuredClone(dev.layouts.desktop) };
    phoneViews.set(dev, v);
  }
  return v.items;
}

/** Call before saving: a phone edit of a layout that still followed the desktop one becomes the phone layout. */
export function adoptPhoneView(dev) {
  if (mode === 'phone' && !dev.layouts.phone) dev.layouts.phone = phoneView(dev);
}

function withWidgets(dev) {
  Object.defineProperty(dev, 'widgets', {
    enumerable: false,   // not stored; dev.layouts is
    configurable: true,
    get() { return mode === 'phone' ? dev.layouts.phone || phoneView(dev) : dev.layouts.desktop; },
    set(items) { if (mode === 'phone') dev.layouts.phone = items; else dev.layouts.desktop = items; },
  });
  return dev;
}

export function normalizeLayouts(desktop, phone) {
  return { desktop: normalizeItems(desktop), phone: Array.isArray(phone) ? normalizeItems(phone) : null };
}

export function normalizeDevice(d) {
  return withWidgets({
    key: d.key || uid(),
    serverId: d.serverId || null,
    url: d.url,
    httpsPort: Number(d.httpsPort) || null,
    name: d.name || d.url,
    token: d.token || null,
    interval: Math.max(MIN_INTERVAL, Number(d.interval) || DEFAULT_INTERVAL),
    // Older versions stored one layout as "widgets".
    layouts: normalizeLayouts(d.layouts?.desktop ?? d.widgets, d.layouts?.phone),
    layoutRev: Number(d.layoutRev) || 0,      // revision of the PC's copy these layouts match
    layoutDirty: !!d.layoutDirty,             // changed here but not yet saved on the PC
  });
}

export function normalizeItems(items) {
  return (Array.isArray(items) ? items : []).map(normalizeItem).filter(Boolean).map((x) => (x.id ? x : { ...x, id: uid() }));
}

export function exportLayout(state) {
  return JSON.stringify({
    app: 'syscom', version: 1, theme: state.theme,
    devices: state.devices.map(({ token, key, layoutRev, layoutDirty, ...rest }) => rest),
  }, null, 2);
}

export function uid() {
  return Math.random().toString(36).slice(2, 10) + Date.now().toString(36).slice(-4);
}

/** "192.168.1.5" → "http://192.168.1.5:47810" (or https://…:47812 when the dashboard itself is on HTTPS) */
export function normalizeUrl(input, secure = location.protocol === 'https:') {
  let s = String(input).trim().replace(/\/+$/, '');
  if (!/^https?:\/\//i.test(s)) s = (secure ? 'https://' : 'http://') + s;
  const u = new URL(s);
  if (!u.port) u.port = u.protocol === 'https:' ? '47812' : '47810';
  return u.origin;
}
