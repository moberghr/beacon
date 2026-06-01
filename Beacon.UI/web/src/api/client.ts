import { BeaconApiClient } from './generated/beacon-api';
import { ensureAntiforgeryPrimed, readCookie } from '@/lib/csrf';

/**
 * Authenticated fetch wrapper used by the generated NSwag client. Primes
 * the antiforgery cookie if absent, attaches the header on mutations, and
 * lets 401s bubble up so callers can redirect to the login page.
 *
 * Antiforgery tokens are bound to the user's claims; if the cookie was
 * minted for a different identity (e.g. logged-in / logged-out churn), the
 * server returns 400 with an antiforgery title. We force-reprime and retry
 * once before letting the failure bubble.
 */
async function beaconFetch(input: RequestInfo, init?: RequestInit): Promise<Response> {
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

let cachedClient: BeaconApiClient | undefined;

export function beaconApi(): BeaconApiClient {
  if (cachedClient === undefined) {
    cachedClient = new BeaconApiClient(window.location.origin, { fetch: beaconFetch });
  }
  return cachedClient;
}

export type { BeaconApiClient } from './generated/beacon-api';
