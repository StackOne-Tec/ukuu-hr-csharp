// Ukuu HR Service Worker — Phase 17: Cache-busting enabled
// The cache name includes a version that changes on every deployment.
// When the browser fetches /service-worker.js?v={timestamp}, it sees a
// new SW and triggers the update flow. The new SW claims all clients
// immediately (no waiting for tabs to close).

// Cache version — must match the ?v= param appended in App.razor.
// The SW reads its own URL from registration to extract the version.
self.addEventListener('message', function(event) {
    if (event.data === 'skipWaiting') {
        self.skipWaiting();
    }
});

const CACHE_NAME = 'ukuu-hr-v' + (new Date().getTime());
const APP_SHELL = [
    '/',
    '/dashboard',
    '/login',
    '/css/ukuu.css',
    '/css/app.css',
    '/manifest.json',
    '/favicon.png'
];

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME).then((cache) => cache.addAll(APP_SHELL).catch(() => {}))
    );
    // Don't skipWaiting here — wait for the message from the page
    // so we can control the reload timing
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) => Promise.all(
            // Delete ALL old caches (any cache name that doesn't match current)
            keys.filter(k => k !== CACHE_NAME).map(k => {
                console.log('[SW] Deleting old cache:', k);
                return caches.delete(k);
            })
        )).then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (event) => {
    const url = new URL(event.request.url);

    // Only handle GET requests for same-origin or static assets
    if (event.request.method !== 'GET') return;

    // Never cache Blazor framework files or API calls — always fetch fresh
    if (url.pathname.includes('/_framework/') || url.pathname.startsWith('/api/')) {
        return; // Let the browser handle it (network-only)
    }

    // Network-first for navigation requests (always get fresh HTML)
    if (event.request.mode === 'navigate') {
        event.respondWith(
            fetch(event.request)
                .then((response) => {
                    const clone = response.clone();
                    caches.open(CACHE_NAME).then(c => c.put(event.request, clone));
                    return response;
                })
                .catch(() => caches.match(event.request).then(r => r || caches.match('/')))
        );
        return;
    }

    // Cache-first for CSS/JS with ?v= param (these are immutable — safe to cache)
    if (url.pathname.includes('/css/') || url.pathname.includes('/_content/')) {
        event.respondWith(
            caches.match(event.request).then((cached) => {
                return cached || fetch(event.request).then((response) => {
                    // Only cache successful responses
                    if (response.ok) {
                        const clone = response.clone();
                        caches.open(CACHE_NAME).then(c => c.put(event.request, clone));
                    }
                    return response;
                });
            })
        );
    }
});
