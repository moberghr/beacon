export function readCookie(name: string): string | null {
  const prefix = `${name}=`;
  for (const cookie of document.cookie.split('; ')) {
    if (cookie.startsWith(prefix)) {
      return decodeURIComponent(cookie.slice(prefix.length));
    }
  }
  return null;
}

let primePromise: Promise<void> | null = null;

/**
 * Issue a GET to /beacon/api/csrf so the server sets both antiforgery cookies
 * (`.Beacon.Antiforgery` HttpOnly + `XSRF-TOKEN` JS-readable) before any
 * mutation goes out. Idempotent and deduped via a cached in-flight promise.
 */
export function ensureAntiforgeryPrimed(force = false): Promise<void> {
  if (!force && readCookie('XSRF-TOKEN') !== null) return Promise.resolve();
  if (!force && primePromise) return primePromise;
  primePromise = fetch('/beacon/api/csrf', { credentials: 'include' })
    .then(() => undefined)
    .catch(() => undefined)
    .finally(() => {
      if (readCookie('XSRF-TOKEN') === null) primePromise = null;
    });
  return primePromise;
}
