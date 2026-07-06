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
    .catch(err => {
      console.warn('[beacon-csrf] failed to prime antiforgery token via /beacon/api/csrf', err);
      return undefined;
    })
    .finally(() => {
      if (readCookie('XSRF-TOKEN') === null) primePromise = null;
    });
  return primePromise;
}

/**
 * Authenticated fetch wrapper shared by the generated NSwag client and
 * `fetchJson`. Primes the antiforgery cookie if absent, attaches the header
 * on mutations, and lets 401s bubble up so callers can redirect to the
 * login page.
 *
 * Antiforgery tokens are bound to the user's claims; if the cookie was
 * minted for a different identity (e.g. logged-in / logged-out churn), the
 * server returns 400 with an antiforgery title. We force-reprime and retry
 * once before letting the failure bubble.
 */
export async function beaconFetch(input: RequestInfo, init?: RequestInit): Promise<Response> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const mutation = method !== 'GET' && method !== 'HEAD';

  const send = async () => {
    const headers = new Headers(init?.headers ?? {});
    if (mutation) {
      if (readCookie('XSRF-TOKEN') === null) {
        await ensureAntiforgeryPrimed();
      }
      const csrf = readCookie('XSRF-TOKEN');
      if (csrf !== null && !headers.has('X-XSRF-TOKEN')) {
        headers.set('X-XSRF-TOKEN', csrf);
      }
    }
    return fetch(input, {
      ...init,
      method,
      headers,
      credentials: 'include',
    });
  };

  const response = await send();
  if (mutation && response.status === 400 && await looksLikeAntiforgeryFailure(response.clone())) {
    await ensureAntiforgeryPrimed(true);
    return send();
  }
  return response;
}

async function looksLikeAntiforgeryFailure(response: Response): Promise<boolean> {
  try {
    const body = await response.text();
    if (!body) return false;
    return body.toLowerCase().includes('antiforgery');
  } catch {
    return false;
  }
}
