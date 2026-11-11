/**
 * The embed page's whole conversation with Beacon: one render call, authenticated by the token the host put in the
 * URL fragment and by the frame marker Beacon put in the served page. Nothing here touches the app's session: no
 * cookies (`credentials: 'omit'`), no antiforgery, no storage. The token lives only in memory, for the page's lifetime.
 */

/** The render route: `POST`, answered with the grant's rows, PII-masked and capped. */
export const RENDER_PATH = '/beacon/api/embed/render';

/** The `Authorization` scheme of an embed token (never `Bearer`). */
export const TOKEN_SCHEME = 'BeaconEmbed';

/** The header every render sends the frame marker back in. */
export const FRAME_HEADER = 'X-Beacon-Embed-Frame';

/** The meta element of the served page that carries the frame marker. */
export const FRAME_MARKER_META = 'beacon-embed-frame';

/** The meta element of the served page that lists the origins the grant lets frame it (its `frame-ancestors`). */
export const FRAME_ORIGINS_META = 'beacon-embed-origins';

/** The message posted to the host page when the token has expired, so it can mint a new one. */
export const EXPIRED_MESSAGE = { type: 'beacon-embed:expired' } as const;

export interface EmbedRender {
  title: string;
  columns: string[];
  rows: Record<string, unknown>[];
  /** The result had more rows than `maxRows`; only the first `maxRows` are here. */
  truncated: boolean;
  maxRows: number;
}

/** What a render call answered: rows, a refused credential (401), or anything else that showed nothing. */
export type RenderOutcome =
  | { kind: 'ok'; data: EmbedRender }
  | { kind: 'refused' }
  | { kind: 'unavailable' };

/**
 * Reads the token from the fragment (`#t=<token>`) and removes the fragment from the address at once, so it is not
 * left in the history entry, a bookmark or a copied link. Returns null when there is none.
 */
export function takeToken(win: Window = window): string | null {
  const hash = win.location.hash;
  if (hash === '') {
    return null;
  }

  win.history.replaceState(null, '', win.location.pathname + win.location.search);
  const token = new URLSearchParams(hash.slice(1)).get('t')?.trim();

  return token ? token : null;
}

/** The frame marker of the served page: only ever from its meta element, never from the address or a message. */
export function readFrameMarker(doc: Document = document): string | null {
  const content = doc.querySelector(`meta[name="${FRAME_MARKER_META}"]`)?.getAttribute('content')?.trim();

  return content ? content : null;
}

/**
 * The origins the grant lets frame the page, from the served page's meta element only (the same list as the response's
 * `frame-ancestors`), never from the address or a message. A value that is not written as an origin is left out.
 */
export function readFrameOrigins(doc: Document = document): string[] {
  const content = doc.querySelector(`meta[name="${FRAME_ORIGINS_META}"]`)?.getAttribute('content') ?? '';

  return content
    .split(/\s+/)
    .filter(x => x !== '')
    .filter(x => originOf(x) === x);
}

/** What `framingOrigin` reads of the page it runs in. */
export interface FramedPage {
  location: { ancestorOrigins?: ArrayLike<string> };
  document: { referrer: string };
}

/**
 * The origin to post to the framing page at: the grant's one origin; with several, the framing page's own origin
 * (`location.ancestorOrigins` where the browser has it, else the referrer's origin) when the grant lists it. Null when it
 * cannot be told, and then nothing is posted: a message never goes to any origin (`'*'`).
 */
export function framingOrigin(allowed: readonly string[], page: FramedPage = window): string | null {
  if (allowed.length <= 1) {
    return allowed[0] ?? null;
  }

  const ancestors = page.location.ancestorOrigins;
  const parent = ancestors != null && ancestors.length > 0 ? ancestors[0] : originOf(page.document.referrer);

  return parent != null && allowed.includes(parent) ? parent : null;
}

/**
 * When the token expires (its `exp` claim), or null when it cannot be read. Only to word a refusal Beacon already gave:
 * the page never decides on its own clock that a token has expired, Beacon verifies it.
 */
export function tokenExpiry(token: string): Date | null {
  const payload = token.split('.')[1];
  if (!payload) {
    return null;
  }

  try {
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const exp = (JSON.parse(atob(base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '='))) as { exp?: unknown })
      .exp;

    return typeof exp === 'number' ? new Date(exp * 1000) : null;
  } catch {
    return null;
  }
}

export function isExpired(token: string, now: Date = new Date()): boolean {
  const expiry = tokenExpiry(token);

  return expiry != null && expiry.getTime() <= now.getTime();
}

/**
 * How the view reads a refusal (Beacon gives every refusal the same answer): as expiry when the token's `exp` has passed,
 * or when the same token rendered before (it was valid, so it has run out or its grant ended); otherwise unavailable.
 */
export function refusalMeansExpired(token: string, renderedBefore: boolean, now: Date = new Date()): boolean {
  return renderedBefore || isExpired(token, now);
}

/**
 * Tells the framing page, at most once per page load, that the token has expired, so a host that mints a new one on
 * the message cannot be driven into a loop. The message carries nothing but its type, and is posted only at
 * `targetOrigin` (an origin the grant lets frame the page, see `framingOrigin`); without one nothing is posted.
 */
export function expiryNotifier(host: Pick<Window, 'postMessage'> | null, targetOrigin: string | null): () => void {
  let sent = false;

  return () => {
    if (sent || host == null || targetOrigin == null) {
      return;
    }

    sent = true;
    host.postMessage(EXPIRED_MESSAGE, targetOrigin);
  };
}

/** Renders the grant's query. Every refusal of the credential is the same generic 401, answered as `refused`. */
export async function renderEmbed(token: string, frameMarker: string, signal?: AbortSignal): Promise<RenderOutcome> {
  let response: Response;
  try {
    response = await fetch(`${window.location.origin}${RENDER_PATH}`, {
      method: 'POST',
      headers: {
        Accept: 'application/json',
        Authorization: `${TOKEN_SCHEME} ${token}`,
        [FRAME_HEADER]: frameMarker,
      },
      credentials: 'omit',
      cache: 'no-store',
      referrerPolicy: 'no-referrer',
      signal,
    });
  } catch (error) {
    if (signal?.aborted) {
      throw error;
    }

    return { kind: 'unavailable' };
  }

  if (response.status === 401) {
    return { kind: 'refused' };
  }

  if (!response.ok) {
    return { kind: 'unavailable' };
  }

  try {
    const data = (await response.json()) as Partial<EmbedRender>;

    return {
      kind: 'ok',
      data: {
        title: data.title ?? '',
        columns: data.columns ?? [],
        rows: data.rows ?? [],
        truncated: data.truncated === true,
        maxRows: data.maxRows ?? 0,
      },
    };
  } catch {
    return { kind: 'unavailable' };
  }
}

/** The origin of an absolute http(s) URL, or null for anything else. */
function originOf(url: string): string | null {
  try {
    const parsed = new URL(url);

    return parsed.protocol === 'https:' || parsed.protocol === 'http:' ? parsed.origin : null;
  } catch {
    return null;
  }
}
