import { beaconFetch } from './csrf';

export { ensureAntiforgeryPrimed } from './csrf';

export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const isMutation = method !== 'GET' && method !== 'HEAD';

  const headers = new Headers(init?.headers ?? {});
  headers.set('Accept', 'application/json');
  if (isMutation) {
    headers.set('Content-Type', headers.get('Content-Type') ?? 'application/json');
  }

  // CSRF priming, header attachment, and the antiforgery-mismatch retry all
  // live in `beaconFetch` — the same wrapper the generated NSwag client uses.
  const response = await beaconFetch(path, {
    ...init,
    method,
    headers,
  });

  if (!response.ok) {
    throw new ApiError(response.status, await safeText(response));
  }

  // 204s and empty-bodied 2xx responses have nothing to parse.
  const text = await safeText(response);
  if (text === '') {
    return undefined as T;
  }
  return JSON.parse(text) as T;
}

async function safeText(response: Response): Promise<string> {
  try {
    return await response.text();
  } catch {
    return '';
  }
}

/**
 * Trust boundary between the generated NSwag client and our hand-written
 * result/command types.
 *
 * The generated DTOs are intentionally loose: `markOptionalProperties` makes
 * every nullable field optional, every interface carries a `[key: string]: any`
 * index signature, and date fields are typed `Date` even though the client
 * deserializes with a plain `JSON.parse` (no reviver) — so at runtime they are
 * strings. Our local interfaces are stricter and more accurate, so we assert
 * the wire payload into them at the call boundary instead of consuming the
 * loose generated shape directly.
 *
 * This is the single, greppable place that bridge happens. If we later add
 * runtime validation (e.g. zod), it goes here and every call site is covered.
 */
export function unwrap<T>(value: unknown): T {
  return value as T;
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
    // The API emits RFC 7807 application/problem+json; surface the human-readable
    // `detail`/`title` rather than dumping the raw JSON body into the toast.
    if (err.body) {
      try {
        const problem = JSON.parse(err.body) as { detail?: string; title?: string };
        const message = problem.detail || problem.title;
        if (message) {
          return message;
        }
      } catch {
        // Body was not JSON — fall through to the raw body.
      }
      return err.body;
    }
    return `${fallback} (${err.status})`;
  }
  if (err instanceof Error) {
    return err.message;
  }
  return fallback;
}
