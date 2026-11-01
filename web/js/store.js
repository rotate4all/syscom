// Device list and this browser's preferences, in localStorage. Each PC's dashboard is saved on that PC (and cached
// here so it shows instantly and works offline); the device list is also kept on the PC that serves the dashboard,
// so another address of the same PC (localhost, its IP, https) or another browser finds it again.
import { normalizeItem, pct } from './widgets.js';

const KEY = 'syscom.v1';

export const DEFAULT_INTERVAL = 1000;
export const MIN_INTERVAL = 500;
/** Polling choices offered in Settings, in ms. */
export const INTERVALS = [500, 1000, 2000, 3000, 5000, 10000];

export function load() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { /* private mode or corrupt */ }
  return {
    theme: saved.theme || 'auto',
    awake: ['auto', 'on', 'off'].includes(saved.awake) ? saved.awake : 'auto',
    active: saved.active || 0,
    preview: !!saved.preview,
    fullscreen: !!saved.fullscreen,
    collapsed: saved.collapsed && typeof saved.collapsed === 'object' ? saved.collapsed : {},
    devices: Array.isArray(saved.devices) ? saved.devices.map(normalizeDevice) : [],
    // Sync of the device list with the PC that serves this page (see app.js, "device list").
    devicesRev: Number(saved.devicesRev) || 0,
    devicesSynced: !!saved.devicesSynced,
    devicesDirty: !!saved.devicesDirty,
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

/** The items of one of a PC's two layouts ('desktop' or 'phone'; the phone one follows the desktop one until split). */
export function layoutItems(dev, which) {
  return which === 'phone' ? dev.layouts.phone || dev.layouts.desktop : dev.layouts.desktop;
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

// ---- the dashboard's appearance (one per PC, shared by its desktop and phone layouts)
export const LOOK_DEFAULTS = Object.freeze({
  bg: 'none',          // none | color | image
  color: '#101826',
  image: null,         // id of the picture stored on the PC (/api/dashboard/background)
  fit: 'cover',        // cover | contain | tile
  dim: 0,              // 0–80 %: fades the picture toward the theme's background, for legibility
  alpha: 100,          // card background opacity, %
  round: 100,          // corner rounding of cards and bars, % of the default (0 = square)
  anim: 'smooth',      // smooth (always) | auto (unless the device asks for reduced motion) | off
});

export function normalizeLook(l) {
  const x = l && typeof l === 'object' ? l : {};
  const pick = (v, list, d) => (list.includes(v) ? v : d);
  return {
    bg: pick(x.bg, ['none', 'color', 'image'], 'none'),
    color: /^#[0-9a-f]{6}$/i.test(x.color) ? x.color : LOOK_DEFAULTS.color,
    image: typeof x.image === 'string' && /^[0-9a-f]{8,64}$/.test(x.image) ? x.image : null,
    fit: pick(x.fit, ['cover', 'contain', 'tile'], 'cover'),
    dim: Math.min(80, pct(x.dim) ?? 0),
    alpha: pct(x.alpha) ?? 100,
    round: pct(x.round) ?? 100,
    anim: pick(x.anim, ['smooth', 'auto', 'off'], 'smooth'),
  };
}

// ---- saved layouts: named copies of a layout to switch between (kept on the PC with its dashboard)
export const MAX_SAVED = 50;

export function normalizeSaved(list) {
  return (Array.isArray(list) ? list : []).filter((x) => x && Array.isArray(x.items)).slice(0, MAX_SAVED).map((x) => ({
    id: typeof x.id === 'string' && x.id ? x.id : uid(),
    name: String(x.name ?? 'Layout').slice(0, 60) || 'Layout',
    mode: x.mode === 'phone' ? 'phone' : 'desktop',
    items: normalizeItems(x.items),
    savedAt: Number(x.savedAt) || 0,
  }));
}

function normalizeCurrent(c) {
  const id = (v) => (typeof v === 'string' && v ? v : null);
  return { desktop: id(c?.desktop), phone: id(c?.phone) };
}

/** Everything of a PC's dashboard as stored on the PC (and cached here). */
export function normalizeDash(d) {
  return {
    layouts: normalizeLayouts(d?.layouts?.desktop ?? d?.widgets, d?.layouts?.phone ?? d?.phone),
    look: normalizeLook(d?.look),
    saved: normalizeSaved(d?.saved),
    current: normalizeCurrent(d?.current),
  };
}

export function normalizeDevice(d) {
  const dash = normalizeDash(d);
  return withWidgets({
    key: d.key || uid(),
    serverId: d.serverId || null,
    url: d.url,
    httpsPort: Number(d.httpsPort) || null,
    name: d.name || d.url,
    token: d.token || null,
    tokenAt: Number(d.tokenAt) || 0,          // when the token was issued (renewed daily while in use)
    interval: Math.max(MIN_INTERVAL, Number(d.interval) || DEFAULT_INTERVAL),
    ...dash,
    layoutRev: Number(d.layoutRev) || 0,      // revision of the PC's copy these layouts match
    layoutDirty: !!d.layoutDirty,             // changed here but not yet saved on the PC
  });
}

export function normalizeItems(items) {
  return (Array.isArray(items) ? items : []).map(normalizeItem).filter(Boolean).map((x) => (x.id ? x : { ...x, id: uid() }));
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
