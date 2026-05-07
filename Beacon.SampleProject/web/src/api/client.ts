import { BeaconApiClient } from './generated/beacon-api';

function readCookie(name: string): string | null {
  const prefix = `${name}=`;
  for (const cookie of document.cookie.split('; ')) {
    if (cookie.startsWith(prefix)) {
      return decodeURIComponent(cookie.slice(prefix.length));
    }
  }
  return null;
}

/**
 * Authenticated fetch wrapper used by the generated NSwag client. Sends
 * cookies, attaches the antiforgery header on mutations, and lets 401s
 * bubble up so callers can redirect to the Blazor login page.
 */
function beaconFetch(input: RequestInfo, init?: RequestInit): Promise<Response> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const headers = new Headers(init?.headers ?? {});

  if (method !== 'GET' && method !== 'HEAD') {
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
