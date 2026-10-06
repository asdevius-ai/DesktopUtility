// Retired service worker.
// Earlier versions cached the whole UI cache-first, which kept serving a stale client after host updates.
// Browsers that still have that worker pick up this file, which clears the caches and removes itself.
self.addEventListener('install', () => self.skipWaiting());

self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        for (const key of await caches.keys()) await caches.delete(key);
        await self.registration.unregister();
        for (const client of await self.clients.matchAll({ type: 'window' })) client.navigate(client.url);
    })());
});
