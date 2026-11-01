// One live connection to a SYSCOM server: login, WebSocket stream, reconnect with backoff.
import { effectiveMaxFrom } from './widgets.js';

const CLOSE_UNAUTHORIZED = 4401;
const DAY = 86_400_000;

/** A PC's /api/info at a base address (direct, or through a relay). */
export async function probe(base, timeoutMs = 6000) {
  const r = await fetch(base + '/api/info', { cache: 'no-store', signal: AbortSignal.timeout(timeoutMs) });
  if (!r.ok) throw new Error(r.status === 404 || r.status === 502 ? "That PC doesn't answer." : 'Not a SYSCOM server');
  return r.json();
}

export class Connection extends EventTarget {
  constructor(device) {
    super();
    this.device = device;
    this.status = 'idle';      // idle | connecting | online | offline | auth
    this.catalog = [];
    this.sensors = new Map();  // id → sensor info
    this.index = new Map();    // id → position in data frames
    this.ws = null;
    this.retry = 0;
    this.timer = 0;
    this.watchAll = false;     // true while the widget editor is open: stream every sensor for its live list
    this.last = null;          // the latest data frame
    this.home = false;         // the PC that served this page
    this.route = null;         // 'direct' | 'relay' | null (not decided yet)
  }

  /**
   * A dashboard opened over HTTPS must reach every PC over HTTPS too (browsers block mixed content). Each PC has its
   * own certificate; when this browser doesn't trust another PC's, it goes through the PC that served the page,
   * which relays to the others on the LAN (/relay/{id}), so trusting that one PC is enough.
   */
  get direct() {
    const url = new URL(this.device.url);
    if (location.protocol === 'https:' && !this.home) return this.device.httpsPort ? `https://${url.hostname}:${this.device.httpsPort}` : null;
    if (location.protocol === 'https:' && this.home) return location.origin;
    return url.origin;
  }

  get relay() {
    const url = new URL(this.device.url);
    return `${location.origin}/relay/${this.device.serverId || `${url.hostname}-${url.port || 47810}`}`;
  }

  get base() { return this.route === 'relay' ? this.relay : this.direct || this.relay; }

  get secure() { return this.base.startsWith('https:'); }

  /** Picks direct or relayed access (only matters for a dashboard opened over HTTPS). */
  async resolveRoute() {
    if (this.route) return this.route;
    if (location.protocol !== 'https:' || this.home || !this.relayAvailable) return (this.route = 'direct');
    if (this.direct) {
      try { await probe(this.direct, 4000); return (this.route = 'direct'); } catch { /* untrusted certificate or unreachable */ }
    }
    try { await probe(this.relay, 6000); return (this.route = 'relay'); } catch { /* not reachable either way */ }
    return null;
  }

  info() { return probe(this.base); }

  async login(password) {
    const r = await fetch(this.base + '/api/login', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ password }),
    });
    if (r.status === 429) throw new Error(`Too many attempts. Try again in ${r.headers.get('retry-after') || 'a few'} seconds.`);
    if (!r.ok) throw new Error('Wrong password.');
    const { token } = await r.json();
    this.device.token = token;
    this.device.tokenAt = Date.now();
    this.emit('token');
    this.connect();
  }

  /** Swaps the token for a fresh one once a day, so a browser in use stays signed in (30 days after its last use). */
  async renewToken() {
    if (!this.device.token || Date.now() - (this.device.tokenAt || 0) < DAY) return;
    try {
      const { token } = await this.api('/api/token', { method: 'POST' });
      if (token) { this.device.token = token; this.device.tokenAt = Date.now(); this.emit('token'); }
    } catch { /* offline: try again on the next connection */ }
  }

  logout() {
    this.device.token = null;
    this.emit('token');
    this.stop();
    this.setStatus('auth');
  }

  async peers() {
    if (!this.device.token) return [];
    const r = await fetch(this.base + '/api/peers', { headers: { authorization: 'Bearer ' + this.device.token } });
    return r.ok ? r.json() : [];
  }

  /** Signed-in API call; a rejected token means signing in again. */
  async api(path, options = {}) {
    if (!this.device.token) throw new Error('Not signed in.');
    const r = await fetch(this.base + path, {
      cache: 'no-store',
      ...options,
      headers: { authorization: 'Bearer ' + this.device.token, ...(options.body ? { 'content-type': 'application/json' } : {}) },
    });
    if (r.status === 401) { this.logout(); throw new Error('Signed out.'); }
    if (!r.ok) throw new Error((await r.json().catch(() => null))?.message || `HTTP ${r.status}`);
    return r.json();
  }

  /** This PC's dashboard: { rev, widgets (desktop), phone (null = same as desktop), look, saved, current } (rev 0 = none saved yet). */
  loadDashboard() { return this.api('/api/dashboard'); }

  /** Saves the dashboard on the PC (both layouts, look, saved layouts); resolves to the new revision. */
  async saveDashboard(dev) {
    const body = JSON.stringify({ widgets: dev.layouts.desktop, phone: dev.layouts.phone, look: dev.look, saved: dev.saved, current: dev.current });
    return (await this.api('/api/dashboard', { method: 'PUT', body })).rev;
  }

  /** The dashboard's background picture as a Blob, or null. */
  async loadBackground() {
    const r = await fetch(this.base + '/api/dashboard/background', { headers: { authorization: 'Bearer ' + this.device.token } });
    return r.ok ? r.blob() : null;
  }

  /** Stores a picture as the background; resolves to its id. */
  async saveBackground(blob) {
    const r = await fetch(this.base + '/api/dashboard/background', {
      method: 'PUT', body: blob, headers: { authorization: 'Bearer ' + this.device.token, 'content-type': blob.type },
    });
    if (!r.ok) throw new Error((await r.json().catch(() => null))?.message || `HTTP ${r.status}`);
    return (await r.json()).id;
  }

  /** The device list kept on this PC (see app.js): { rev, devices: [{ id, name, url, httpsPort }] }. */
  loadDevices() { return this.api('/api/devices'); }

  async saveDevices(devices) {
    return (await this.api('/api/devices', { method: 'PUT', body: JSON.stringify({ devices }) })).rev;
  }

  /** One sensor's history: { step, start, avg[], min[], max[], enabled }. */
  history(sensor, days) { return this.api(`/api/history?sensor=${encodeURIComponent(sensor)}&days=${days}`); }

  async connect() {
    this.stop();
    if (!this.device.token) { this.setStatus('auth'); return; }
    this.setStatus('connecting');
    const attempt = ++this.attempt;
    await this.resolveRoute();
    if (attempt !== this.attempt) return;   // stopped or restarted meanwhile

    const url = this.base.replace(/^http/, 'ws') + '/ws?token=' + encodeURIComponent(this.device.token);
    const ws = new WebSocket(url);
    this.ws = ws;

    ws.onopen = () => {
      this.retry = 0;
      this.setStatus('online');
      this.subscribe();
      this.renewToken();
    };
    ws.onmessage = (e) => this.onMessage(e.data);
    ws.onclose = (e) => {
      if (this.ws !== ws) return; // superseded or stopped on purpose
      this.ws = null;
      if (e.code === CLOSE_UNAUTHORIZED) {
        this.device.token = null;
        this.emit('token');
        this.setStatus('auth');
        return;
      }
      this.setStatus('offline');
      this.route = null;   // decide again next time (a certificate may have been trusted, or the relay gone)
      const delay = Math.min(30000, 1000 * 2 ** this.retry++);
      this.timer = setTimeout(() => this.connect(), delay);
    };
  }

  stop() {
    this.attempt = (this.attempt || 0) + 1;
    clearTimeout(this.timer);
    const ws = this.ws;
    this.ws = null;
    if (ws) ws.close();
    if (this.status !== 'auth') this.setStatus('idle');
  }

  /** Ask only for the sensors this device's dashboard shows, at its polling interval (all of them while editing). */
  subscribe() {
    if (this.ws?.readyState !== WebSocket.OPEN) return;
    if (this.watchAll) {   // no list = every sensor
      this.ws.send(JSON.stringify({ type: 'subscribe', interval: this.device.interval }));
      return;
    }
    // Each widget's sensor, its extra readings, and the sensors that give it a live scale maximum (e.g. total VRAM).
    const sensors = [...new Set(this.device.widgets.filter((w) => w.sensor).flatMap((w) =>
      [w.sensor, ...(w.extra || []).map((x) => x.sensor), ...(effectiveMaxFrom(w, this.sensors.get(w.sensor), this.catalog) || [])]))];
    this.ws.send(JSON.stringify({ type: 'subscribe', interval: this.device.interval, sensors }));
  }

  /** Streams every sensor (on) or just the dashboard's again (off). */
  setWatchAll(on) {
    if (this.watchAll === on) return;
    this.watchAll = on;
    this.subscribe();
  }

  onMessage(raw) {
    let m;
    try { m = JSON.parse(raw); } catch { return; }
    switch (m.type) {
      case 'catalog':
        this.catalog = m.sensors;
        this.sensors = new Map(m.sensors.map((s) => [s.id, s]));
        this.emit('catalog');
        break;
      case 'subscribed':
        this.index = new Map(m.sensors.map((id, i) => [id, i]));
        break;
      case 'data':
        this.last = { t: m.t, v: m.v, index: this.index };   // the order of the values in this frame
        this.emit('data', this.last);
        break;
      case 'layout':
        this.emit('layout', { rev: m.rev });
        break;
      case 'devices':
        this.emit('devices', { rev: m.rev });
        break;
    }
  }

  value(frame, sensorId) {
    const i = (frame.index || this.index).get(sensorId);
    return i === undefined ? null : frame.v[i];
  }

  setStatus(s) {
    if (this.status === s) return;
    this.status = s;
    this.emit('status');
  }

  emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }
}
