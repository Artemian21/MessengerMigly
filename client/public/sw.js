/* Migly service worker: offline app shell (NFR-11) and Web Push (NTF-01). API calls are never cached. */
const SHELL_CACHE = 'migly-shell-v1'
const SHELL = ['/', '/index.html', '/manifest.webmanifest', '/favicon.svg']

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(SHELL_CACHE).then((cache) => cache.addAll(SHELL)).then(() => self.skipWaiting()))
})

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((key) => key !== SHELL_CACHE).map((key) => caches.delete(key))))
      .then(() => self.clients.claim()),
  )
})

self.addEventListener('fetch', (event) => {
  const request = event.request
  const url = new URL(request.url)
  if (request.method !== 'GET' || url.origin !== self.location.origin) return
  if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/hubs/') || url.pathname.startsWith('/health')) return

  if (request.mode === 'navigate') {
    // Network first for the page; fall back to the cached shell when offline.
    event.respondWith(fetch(request).catch(() => caches.match('/index.html')))
    return
  }

  if (url.pathname.startsWith('/assets/')) {
    // Hashed build assets are immutable: cache first.
    event.respondWith(
      caches.match(request).then(
        (cached) =>
          cached ||
          fetch(request).then((response) => {
            const copy = response.clone()
            caches.open(SHELL_CACHE).then((cache) => cache.put(request, copy))
            return response
          }),
      ),
    )
  }
})

self.addEventListener('push', (event) => {
  let payload = { title: 'Migly', body: 'New message', url: '/', tag: 'migly' }
  try {
    payload = { ...payload, ...event.data.json() }
  } catch {
    // Keep the generic notification.
  }
  event.waitUntil(
    self.registration.showNotification(payload.title, {
      body: payload.body,
      tag: payload.tag,
      renotify: true,
      icon: '/icon-192.png',
      data: { url: payload.url },
    }),
  )
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const target = (event.notification.data && event.notification.data.url) || '/'
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((windows) => {
      for (const client of windows) {
        if ('focus' in client) {
          client.navigate(target)
          return client.focus()
        }
      }
      return self.clients.openWindow(target)
    }),
  )
})
