import { BeaconApiClient } from './generated/beacon-api';
import { ensureAntiforgeryPrimed, readCookie } from '@/lib/csrf';

/**
 * Authenticated fetch wrapper used by the generated NSwag client. Primes
 * the antiforgery cookie if absent, attaches the header on mutations, and
 * lets 401s bubble up so callers can redirect to the login page.
 */
async function beaconFetch(input: RequestInfo, init?: RequestInit): Promise<Response> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const headers = new Headers(init?.headers ?? {});

  if (method !== 'GET' && method !== 'HEAD') {
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
}

let cachedClient: BeaconApiClient | undefined;

export function beaconApi(): BeaconApiClient {
  if (cachedClient === undefined) {
    cachedClient = new BeaconApiClient(window.location.origin, { fetch: beaconFetch });
  }
  return cachedClient;
}

export type { BeaconApiClient } from './generated/beacon-api';
