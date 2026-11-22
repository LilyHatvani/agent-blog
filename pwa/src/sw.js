// The app's service worker: the shell cached for a quick start and an offline notice; the API is never cached
// (the server decides, the app shows). Filled in at build time (vite.config.ts).
const PRECACHE = __PRECACHE__;
const VERSION = __VERSION__;
const CACHE = `agent-blog-${VERSION}`;

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k.startsWith('agent-blog-') && k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || url.origin !== location.origin || !url.pathname.startsWith('/admin/')) return;
  // Hashed bundles: cache first. Everything else (the shell page): network first, so a deploy shows at once.
  if (url.pathname.startsWith('/admin/assets/')) {
    event.respondWith(caches.match(event.request).then((hit) => hit ?? fetch(event.request)));
  } else {
    event.respondWith(fetch(event.request).catch(() => caches.match(event.request).then((hit) => hit ?? Response.error())));
  }
});

// Notifications (docs/specs/notifications.md): the server sends {title, body, url, tag}, encrypted for this phone.
// A tap opens the app at that place, reusing an open window.
self.addEventListener('push', (event) => {
  let m = { title: 'Agent Blog', body: '', url: '/admin/', tag: undefined };
  try { m = { ...m, ...event.data.json() }; } catch { /* an empty or unreadable push still shows something */ }
  event.waitUntil(self.registration.showNotification(m.title, {
    body: m.body, tag: m.tag, data: { url: m.url || '/admin/' }, icon: '/admin/icon.svg', badge: '/admin/icon.svg',
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = new URL(event.notification.data?.url || '/admin/', location.origin);
  if (url.origin !== location.origin) return; // only ever opens this blog's own app
  event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((wins) => {
    const open = wins.find((w) => new URL(w.url).pathname.startsWith('/admin'));
    if (open) return open.navigate(url.href).then((w) => (w ?? open).focus());
    return self.clients.openWindow(url.href);
  }));
});
