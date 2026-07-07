// Ukuu HR Service Worker — PWA support
// Enables installable app on Windows + Mac via Chrome/Edge
// Caches the app shell for offline use

const CACHE_NAME = 'ukuu-hr-v1.0.0';
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
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys().then((keys) => Promise.all(
            keys.filter(k => k !== CACHE_NAME).map(k => caches.delete(k))
        ))
    );
    self.clients.claim();
});

self.addEventListener('fetch', (event) => {
    // Only handle GET requests
    if (event.request.method !== 'GET') return;

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

    // Cache-first for static assets
    if (event.request.url.includes('/css/') || event.request.url.includes('/_content/') || event.request.url.includes('/_framework/')) {
        event.respondWith(
            caches.match(event.request).then((cached) => {
                return cached || fetch(event.request).then((response) => {
                    const clone = response.clone();
                    caches.open(CACHE_NAME).then(c => c.put(event.request, clone));
                    return response;
                });
            })
        );
    }
});
