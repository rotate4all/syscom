// One live connection to a SYSCOM server: login, WebSocket stream, reconnect with backoff.
import { effectiveMaxFrom, intervalOf } from './widgets.js';

const CLOSE_UNAUTHORIZED = 4401;
const DAY = 86_400_000;
const OPEN_TIMEOUT = 10_000;   // a connection that isn't open by then is given up and tried again
const TICK = 1000;             // how often a connection checks that its PC is still sending

/**
 * How long a connection may stay silent before its PC counts as gone. Every PC sends the dashboard's stream at the
 * dashboard's interval (an empty frame if no card uses it), so silence means the PC is gone even though the
 * connection still looks open: a PC that's switched off (Windows' fast startup shuts down without closing
 * connections), crashed or lost its network never says goodbye, and the dashboard never sends anything that would
 * find out. Browsers notice that only after many minutes.
 */
const silentLimit = (interval) => Math.max(5000, 3 * interval + 1000);

/** A PC's /api/info at a base address (direct, or through a relay). */
export async function probe(base, timeoutMs = 6000) {
  const r = await fetch(base + '/api/info', { cache: 'no-store', signal: AbortSignal.timeout(timeoutMs) });
  if (!r.ok) throw new Error(r.status === 404 || r.status === 502 ? "That PC doesn't answer." : 'Not a SYSCOM server');
  return r.json();
}

/** True for a request that never got an answer (no network, refused, untrusted certificate, timed out). */
export function unreachable(ex) {
  return ex instanceof TypeError || ex?.name === 'TimeoutError' || ex?.name === 'AbortError';
}

/**
 * The ways the PC that served this page can name another PC in /relay/{target}: its server id (followed to its
 * current address through LAN discovery and the stored device list), then the address it was added with, as
 * "a.b.c.d-port" or "name-port" (for when this PC hasn't heard from it lately).
 */
export function relayTargets(serverId, url) {
  const u = new URL(url);
  return [...new Set([serverId, `${u.hostname}-${u.port || 47810}`].filter(Boolean))];
}

/** The first relayed address that answers: { info, target }; throws the first error if none does. */
export async function probeRelay(targets, timeoutMs = 6000) {
  let first = null;
  for (const target of targets) {
    try { return { info: await probe(`${location.origin}/relay/${target}`, timeoutMs), target }; } catch (ex) { first ??= ex; }
  }
  throw first || new Error("That PC doesn't answer.");
}

export class Connection extends EventTarget {
  constructor(device) {
    super();
    this.device = device;
    this.status = 'idle';      // idle | connecting | online | offline | auth
    this.catalog = [];
    this.sensors = new Map();  // id → sensor info
    this.index = new Map();    // id → position in data frames (single-rate form; see streams)
    this.streams = new Map();  // stream id (= its interval in ms) → Map id → position in that stream's frames
    this.multi = false;        // the PC sends several rates (0.18+); older PCs send everything at one rate
    this.values = new Map();   // id → latest value, from whichever stream brought it
    this.ws = null;
    this.retry = 0;
    this.timer = 0;
    this.watchAll = false;     // true while the widget editor is open: stream every sensor for its live list
    this.last = null;          // the latest readings of every stream: { t, index: null } (see value())
    this.home = false;         // the PC that served this page
    this.route = null;         // 'direct' | 'relay' | null (not decided yet)
    this.relayTarget = null;   // how the relay names this PC (see relayTargets); null = the first way
    this.lastRoute = null;     // the route of the last connection that opened (kept while offline, for the page)
    this.heardAt = 0;          // performance.now() of the last message (or of the open)
    this.dataAt = 0;           // Date.now() of the last readings, for "no readings since …"
    this.watchdog = 0;
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

  get relay() { return `${location.origin}/relay/${this.relayTarget || this.relayTargets[0]}`; }

  get relayTargets() { return relayTargets(this.device.serverId, this.device.url); }

  get base() { return this.route === 'relay' ? this.relay : this.direct || this.relay; }

  get secure() { return this.base.startsWith('https:'); }

  /**
   * Picks direct or relayed access: directly when this browser can reach the PC itself, through the PC that served
   * the page otherwise. Over HTTPS that's when this browser doesn't trust the other PC's certificate; over plain HTTP
   * when the browser can't reach it (another network, blocked). Every call that talks to the PC waits for this, the
   * sign-in included: until it's decided, `base` is the direct address, which fails for an untrusted certificate.
   */
  resolveRoute() {
    if (this.route) return Promise.resolve(this.route);
    if (this.home || !this.relayAvailable) return Promise.resolve(this.route = 'direct');
    // One decision at a time: the sign-in and the connection may both ask.
    this.resolving ||= (async () => {
      if (this.direct) {
        try { await probe(this.direct, 4000); return (this.route = 'direct'); } catch { /* untrusted certificate or unreachable */ }
      }
      try {
        const { target } = await probeRelay(this.relayTargets, 6000);
        this.relayTarget = target;
        return (this.route = 'relay');
      } catch { return null; /* not reachable either way */ }
    })().finally(() => { this.resolving = null; });
    return this.resolving;
  }

  /** Decide again next time (a certificate may have been trusted, the relay gone, or the PC moved). */
  forgetRoute() {
    if (this.home) return;
    this.route = null;
    this.relayTarget = null;
  }

  /**
   * While this PC is reached through the relay, checks whether this browser can reach it by itself now (its
   * certificate was trusted meanwhile, or it's back on this network) and if so moves the connection there, so the PC
   * doesn't depend on the one that served the page. Called now and then, and when the page is looked at again.
   */
  async tryDirect() {
    if (this.route !== 'relay' || this.status !== 'online' || !this.direct || this.checkingDirect) return false;
    this.checkingDirect = true;
    try {
      await probe(this.direct, 4000);
    } catch {
      return false;   // still untrusted or unreachable: stay on the relay
    } finally {
      this.checkingDirect = false;
    }
    if (this.route !== 'relay' || this.status !== 'online') return false;
    this.route = 'direct';
    this.relayTarget = null;
    this.connect();
    return true;
  }

  info() { return probe(this.base); }

  async login(password) {
    let r;
    try {
      await this.resolveRoute();
      r = await fetch(this.base + '/api/login', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ password }),
      });
    } catch (ex) {
      if (!unreachable(ex)) throw ex;
      this.forgetRoute();
      throw new Error(`Can't reach ${this.device.name}. Check that it's switched on and SYSCOM is running there, then try again.`);
    }
    if (r.status === 429) throw new Error(`Too many attempts. Try again in ${r.headers.get('retry-after') || 'a few'} seconds.`);
    if (r.status === 401) throw new Error('Wrong password.');
    if (!r.ok) {
      const why = (await r.json().catch(() => null))?.message;
      if (r.status !== 404 && r.status !== 502) throw new Error(why || `Sign-in failed (HTTP ${r.status}).`);
      // The relay answers for a PC it can't reach (404 unknown PC, 502 no answer).
      this.forgetRoute();
      throw new Error(`Can't reach ${this.device.name}${why ? ` (${why.replace(/\.$/, '')})` : ''}. Try again in a moment.`);
    }
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
    if (!this.device.token || this.status !== 'online') return [];
    const r = await fetch(this.base + '/api/peers', { headers: { authorization: 'Bearer ' + this.device.token } });
    return r.ok ? r.json() : [];
  }

  /** Signed-in API call; a rejected token means signing in again. */
  async api(path, options = {}) {
    if (!this.device.token) throw new Error('Not signed in.');
    await this.resolveRoute();
    const r = await fetch(this.base + path, {
      cache: 'no-store',
      ...options,
      headers: { authorization: 'Bearer ' + this.device.token, ...(options.body ? { 'content-type': 'application/json' } : {}) },
    });
    if (r.status === 401) { this.logout(); throw new Error('Signed out.'); }
    if (!r.ok) throw new Error((await r.json().catch(() => null))?.message || `HTTP ${r.status}`);
    return r.json();
  }

  /** This PC's dashboard: { rev, widgets (desktop), phone (null = same as desktop), look, saved, current, osd, osdLook } (rev 0 = none saved yet). */
  loadDashboard() { return this.api('/api/dashboard'); }

  /** Saves the dashboard on the PC (the three layouts, look, saved layouts, the OSD's look); resolves to the new revision. */
  async saveDashboard(dev) {
    const body = JSON.stringify({
      widgets: dev.layouts.desktop, phone: dev.layouts.phone, look: dev.look, saved: dev.saved, current: dev.current,
      osd: dev.layouts.osd, osdLook: dev.osdLook,
    });
    return (await this.api('/api/dashboard', { method: 'PUT', body })).rev;
  }

  /**
   * The on-screen display on this PC: { shown, hotkey, tray: { running, hotkeyRegistered, hotkeyError, screens } }
   * (without readings: the dashboard has its own). { unsupported: true } from a PC older than 0.19.
   */
  async osdStatus() {
    try { return await this.api('/api/osd?readings=false'); } catch (ex) {
      if (/HTTP 404/.test(ex.message)) return { unsupported: true };
      throw ex;
    }
  }

  /** The fonts installed on the PC (its tray app reports them); [] when that isn't known. */
  async osdFonts() { return (await this.api('/api/osd/fonts')).fonts || []; }

  /** Shows or hides the on-screen display on the PC's screen; resolves to whether it shows now. */
  async setOsdShown(on) { return (await this.api(`/api/osd/${on ? 'show' : 'hide'}`, { method: 'POST' })).shown; }

  /** The dashboard's background picture as a Blob, or null. */
  async loadBackground() {
    await this.resolveRoute();
    const r = await fetch(this.base + '/api/dashboard/background', { headers: { authorization: 'Bearer ' + this.device.token } });
    return r.ok ? r.blob() : null;
  }

  /** Stores a picture as the background; resolves to its id. */
  async saveBackground(blob) {
    await this.resolveRoute();
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
    this.halt();
    if (!this.device.token) { this.setStatus('auth'); return; }
    // A PC that was lost stays "offline" (its notice stays up) until it's back, through every retry.
    if (this.status !== 'offline') this.setStatus('connecting');
    const attempt = ++this.attempt;
    await this.resolveRoute();
    if (attempt !== this.attempt) return;   // stopped or restarted meanwhile

    const url = this.base.replace(/^http/, 'ws') + '/ws?token=' + encodeURIComponent(this.device.token);
    const ws = new WebSocket(url);
    this.ws = ws;
    this.heardAt = performance.now();
    this.startWatchdog(ws);

    ws.onopen = () => {
      if (this.ws !== ws) return;
      this.retry = 0;
      this.heardAt = performance.now();
      this.lastRoute = this.route || (this.base === this.direct ? 'direct' : 'relay');
      this.setStatus('online');
      this.subscribe();
      this.renewToken();
    };
    ws.onmessage = (e) => {
      if (this.ws !== ws) return;
      this.heardAt = performance.now();
      this.onMessage(e.data);
    };
    ws.onclose = (e) => {
      if (this.ws !== ws) return; // superseded or stopped on purpose
      if (e.code === CLOSE_UNAUTHORIZED) {
        this.halt();
        this.device.token = null;
        this.emit('token');
        this.setStatus('auth');
        return;
      }
      this.lost();
    };
  }

  /**
   * Checks every second that the PC still sends: not open within OPEN_TIMEOUT, or silent for longer than
   * silentLimit, means it's gone, and the connection is dropped and tried again (see silentLimit). A tick that comes
   * late means this page wasn't running (the phone slept, the browser froze the tab): the silence is measured from
   * then, since nothing could have arrived meanwhile.
   */
  startWatchdog(ws) {
    clearInterval(this.watchdog);
    let tickAt = performance.now();
    this.watchdog = setInterval(() => {
      if (this.ws !== ws) { clearInterval(this.watchdog); return; }
      const now = performance.now();
      if (now - tickAt > 3 * TICK) this.heardAt = Math.max(this.heardAt, now);
      tickAt = now;
      const limit = ws.readyState === WebSocket.OPEN ? silentLimit(this.device.interval) : OPEN_TIMEOUT;
      if (now - this.heardAt > limit) this.lost();
    }, TICK);
  }

  /** The PC is gone (connection closed, or silent): show it as offline and try again, with growing pauses. */
  lost() {
    this.halt();
    this.setStatus('offline');
    this.forgetRoute();
    const delay = Math.min(30000, 1000 * 2 ** this.retry++);
    this.timer = setTimeout(() => this.connect(), delay);
  }

  /** Tries again now instead of waiting for the next retry (the page was looked at again, or the relay is back). */
  retryNow() {
    if (this.status !== 'offline' && this.status !== 'idle') return;
    this.retry = 0;
    this.connect();
  }

  /** Closes the connection and cancels retries, without changing the status. */
  halt() {
    this.attempt = (this.attempt || 0) + 1;
    clearTimeout(this.timer);
    clearInterval(this.watchdog);
    const ws = this.ws;
    this.ws = null;
    if (ws) {
      ws.onopen = ws.onmessage = ws.onclose = null;
      try { ws.close(); } catch { /* already closing */ }
    }
  }

  stop() {
    this.halt();
    if (this.status !== 'auth') this.setStatus('idle');
  }

  /**
   * Asks only for the sensors this device's dashboard shows: one stream per refresh interval in use (the dashboard's,
   * plus any card that has its own), each stream's id being its interval. While the widget editor is open, the
   * dashboard's stream carries every sensor for the editor's live list. `interval` + `sensors` (all of them) are sent
   * too, for PCs older than 0.18 that only know one rate; those simply refresh every card at the dashboard's.
   */
  subscribe() {
    if (this.ws?.readyState !== WebSocket.OPEN) return;
    const base = this.device.interval;
    const groups = new Map([[base, new Set()]]);
    for (const w of this.device.widgets) {
      if (!w.sensor) continue;
      const iv = intervalOf(w, base);
      if (!groups.has(iv)) groups.set(iv, new Set());
      for (const id of this.sensorsOf(w)) groups.get(iv).add(id);
    }
    const streams = [...groups].filter(([iv, set]) => set.size || iv === base)
      .map(([iv, set]) => ({ id: iv, interval: iv, sensors: [...set] }));
    if (this.watchAll) delete streams.find((x) => x.id === base).sensors;   // no list = every sensor
    const all = this.watchAll ? undefined : [...new Set(streams.flatMap((x) => x.sensors))];
    this.ws.send(JSON.stringify({ type: 'subscribe', interval: base, sensors: all, streams }));
  }

  /** A card's sensor, its extra readings, and the sensors that give it a live scale maximum (e.g. total VRAM). */
  sensorsOf(w) {
    if (!w.type) return [w.sensor];   // a reading of the OSD
    return [w.sensor, ...(w.extra || []).map((x) => x.sensor), ...(effectiveMaxFrom(w, this.sensors.get(w.sensor), this.catalog) || [])];
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
      case 'subscribed': {
        this.multi = Array.isArray(m.streams);
        this.streams = new Map((m.streams || []).map((x) => [x.id, new Map(x.sensors.map((id, i) => [id, i]))]));
        this.index = this.multi ? new Map() : new Map(m.sensors.map((id, i) => [id, i]));
        // Forget readings nobody streams any more (an old value would show up on a card added later).
        const live = new Set([...this.index.keys(), ...[...this.streams.values()].flatMap((x) => [...x.keys()])]);
        for (const id of this.values.keys()) if (!live.has(id)) this.values.delete(id);
        break;
      }
      case 'data': {
        // s = the stream (its interval) when the PC sends several rates; none = the single-rate form.
        const index = m.s == null ? this.index : this.streams.get(m.s);
        if (!index) break;
        const frame = { t: m.t, v: m.v, s: m.s ?? null, index };
        this.dataAt = Date.now();
        for (const [id, i] of index) this.values.set(id, m.v[i]);
        this.last = { t: m.t, v: null, s: null, index: null };
        this.emit('data', frame);
        break;
      }
      case 'layout':
        this.emit('layout', { rev: m.rev });
        break;
      case 'devices':
        this.emit('devices', { rev: m.rev });
        break;
      case 'osd':
        this.emit('osd', { rev: m.rev, shown: !!m.shown });
        break;
    }
  }

  /** A sensor's value in a frame; a frame without an index (this.last) means the latest value from any stream. */
  value(frame, sensorId) {
    if (!frame.index) return this.values.get(sensorId) ?? null;
    const i = frame.index.get(sensorId);
    return i === undefined ? null : frame.v[i];
  }

  setStatus(s) {
    if (this.status === s) return;
    this.status = s;
    this.emit('status');
  }

  emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }
}
