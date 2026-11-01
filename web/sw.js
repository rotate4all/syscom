// Service worker: makes the dashboard installable and lets it open (from cache) while the PC is unreachable.
// Network first, so a dashboard update on the server shows up on the next load.
const CACHE = 'syscom-shell-v3';
const SHELL = ['./', 'index.html', 'app.css', 'js/app.js', 'js/connection.js', 'js/store.js', 'js/widgets.js', 'js/dnd.js', 'js/history.js', 'js/transfer.js',
  'manifest.webmanifest', 'icon.svg', 'icons/icon-192.png', 'icons/icon-512.png'];

self.addEventListener('install', (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (e) => {
  e.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  // Only the app shell from this origin; never API calls, other SYSCOM servers or the relay to them.
  if (e.request.method !== 'GET' || url.origin !== location.origin || url.pathname === '/ws'
    || url.pathname.startsWith('/api/') || url.pathname.startsWith('/relay/')) return;
  e.respondWith(
    fetch(e.request)
      .then((resp) => {
        if (resp.ok) { const copy = resp.clone(); caches.open(CACHE).then((c) => c.put(e.request, copy)); }
        return resp;
      })
      .catch(() => caches.match(e.request).then((hit) => hit || caches.match('index.html'))),
  );
});
