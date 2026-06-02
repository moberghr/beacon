import { ensureAntiforgeryPrimed, readCookie } from './csrf';

export { ensureAntiforgeryPrimed } from './csrf';

export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const isMutation = method !== 'GET' && method !== 'HEAD';

  const headers = new Headers(init?.headers ?? {});
  headers.set('Accept', 'application/json');
  if (isMutation) {
    headers.set('Content-Type', headers.get('Content-Type') ?? 'application/json');
    if (readCookie('XSRF-TOKEN') === null) {
      await ensureAntiforgeryPrimed();
    }
    const csrf = readCookie('XSRF-TOKEN');
    if (csrf !== null) {
      headers.set('X-XSRF-TOKEN', csrf);
    }
  }

  const response = await fetch(path, {
    ...init,
    method,
    headers,
    credentials: 'include',
  });

  if (!response.ok) {
    throw new ApiError(response.status, await safeText(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function safeText(response: Response): Promise<string> {
  try {
    return await response.text();
  } catch {
    return '';
  }
}

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly body: string) {
    super(`HTTP ${status}: ${body}`);
    this.name = 'ApiError';
  }
}

/**
 * Normalizes any thrown value into a user-facing error message. Prefers
 * the server-supplied `ApiError.body` (RFC 7807 detail), falls back to
 * the standard `Error.message`, and finally to the caller's fallback
 * string. Use at every mutation `onError` boundary so error UX stays
 * consistent.
 */
export function describeError(err: unknown, fallback: string): string {
  if (err instanceof ApiError) {
    return err.body || `${fallback} (${err.status})`;
  }
  if (err instanceof Error) {
    return err.message;
  }
  return fallback;
}
