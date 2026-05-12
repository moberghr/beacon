function readCookie(name: string): string | null {
  const prefix = `${name}=`;
  for (const cookie of document.cookie.split('; ')) {
    if (cookie.startsWith(prefix)) {
      return decodeURIComponent(cookie.slice(prefix.length));
    }
  }
  return null;
}

export async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? 'GET').toUpperCase();
  const isMutation = method !== 'GET' && method !== 'HEAD';

  const headers = new Headers(init?.headers ?? {});
  headers.set('Accept', 'application/json');
  if (isMutation) {
    headers.set('Content-Type', headers.get('Content-Type') ?? 'application/json');
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
