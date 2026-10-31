// One live connection to a SYSCOM server: login, WebSocket stream, reconnect with backoff.
const CLOSE_UNAUTHORIZED = 4401;

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
  }

  /** A dashboard opened over HTTPS must reach every PC over HTTPS too (browsers block mixed content). */
  get base() {
    const url = new URL(this.device.url);
    if (location.protocol === 'https:' && this.device.httpsPort) return `https://${url.hostname}:${this.device.httpsPort}`;
    return url.origin;
  }

  get secure() { return this.base.startsWith('https:'); }

  async info() {
    const r = await fetch(this.base + '/api/info', { cache: 'no-store' });
    if (!r.ok) throw new Error('Not a SYSCOM server');
    return r.json();
  }

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
    this.emit('token');
    this.connect();
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

  connect() {
    this.stop();
    if (!this.device.token) { this.setStatus('auth'); return; }
    this.setStatus('connecting');

    const url = this.base.replace(/^http/, 'ws') + '/ws?token=' + encodeURIComponent(this.device.token);
    const ws = new WebSocket(url);
    this.ws = ws;

    ws.onopen = () => {
      this.retry = 0;
      this.setStatus('online');
      this.subscribe();
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
      const delay = Math.min(30000, 1000 * 2 ** this.retry++);
      this.timer = setTimeout(() => this.connect(), delay);
    };
  }

  stop() {
    clearTimeout(this.timer);
    const ws = this.ws;
    this.ws = null;
    if (ws) ws.close();
    if (this.status !== 'auth') this.setStatus('idle');
  }

  /** Ask only for the sensors this device's dashboard shows, at its polling interval. */
  subscribe() {
    if (this.ws?.readyState !== WebSocket.OPEN) return;
    const sensors = [...new Set(this.device.widgets.map((w) => w.sensor))];
    this.ws.send(JSON.stringify({ type: 'subscribe', interval: this.device.interval, sensors }));
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
        this.emit('data', { t: m.t, v: m.v });
        break;
    }
  }

  value(frame, sensorId) {
    const i = this.index.get(sensorId);
    return i === undefined ? null : frame.v[i];
  }

  setStatus(s) {
    if (this.status === s) return;
    this.status = s;
    this.emit('status');
  }

  emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }
}
