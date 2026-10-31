// Layout and device list, persisted per browser in localStorage.
const KEY = 'syscom.v1';

export const DEFAULT_INTERVAL = 1000;
export const MIN_INTERVAL = 500;

export function load() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(KEY) || '{}'); } catch { /* private mode or corrupt */ }
  return {
    theme: saved.theme || 'auto',
    active: saved.active || 0,
    devices: Array.isArray(saved.devices) ? saved.devices.map(normalizeDevice) : [],
  };
}

export function save(state) {
  try { localStorage.setItem(KEY, JSON.stringify(state)); } catch { /* storage unavailable */ }
}

export function normalizeDevice(d) {
  return {
    key: d.key || uid(),
    serverId: d.serverId || null,
    url: d.url,
    name: d.name || d.url,
    token: d.token || null,
    interval: Math.max(MIN_INTERVAL, Number(d.interval) || DEFAULT_INTERVAL),
    widgets: Array.isArray(d.widgets) ? d.widgets : [],
  };
}

export function exportLayout(state) {
  return JSON.stringify({
    app: 'syscom', version: 1, theme: state.theme,
    devices: state.devices.map(({ token, key, ...rest }) => rest),
  }, null, 2);
}

export function uid() {
  return Math.random().toString(36).slice(2, 10) + Date.now().toString(36).slice(-4);
}

/** "192.168.1.5" → "http://192.168.1.5:47810" */
export function normalizeUrl(input) {
  let s = String(input).trim().replace(/\/+$/, '');
  if (!/^https?:\/\//i.test(s)) s = 'http://' + s;
  const u = new URL(s);
  if (!u.port && u.protocol === 'http:') u.port = '47810';
  return u.origin;
}
