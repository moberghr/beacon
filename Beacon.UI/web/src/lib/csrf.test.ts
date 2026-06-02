import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { readCookie, ensureAntiforgeryPrimed } from './csrf';

function setCookies(jar: string) {
  Object.defineProperty(document, 'cookie', {
    configurable: true,
    get: () => jar,
    set: () => {
      /* no-op for tests */
    },
  });
}

describe('readCookie', () => {
  afterEach(() => {
    setCookies('');
  });

  it('returns null when cookie is absent', () => {
    setCookies('');
    expect(readCookie('XSRF-TOKEN')).toBeNull();
  });

  it('returns the decoded value when cookie is present', () => {
    setCookies('XSRF-TOKEN=abc%20123; other=ignored');
    expect(readCookie('XSRF-TOKEN')).toBe('abc 123');
  });

  it('matches the named cookie even if not first', () => {
    setCookies('first=one; XSRF-TOKEN=token; last=two');
    expect(readCookie('XSRF-TOKEN')).toBe('token');
  });
});

describe('ensureAntiforgeryPrimed', () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchSpy = vi.fn(() => Promise.resolve(new Response(null, { status: 204 })));
    vi.stubGlobal('fetch', fetchSpy);
    setCookies('');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('skips the network call when the cookie is already present', async () => {
    setCookies('XSRF-TOKEN=already');
    await ensureAntiforgeryPrimed();
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it('issues a GET to /beacon/api/csrf when the cookie is missing', async () => {
    await ensureAntiforgeryPrimed(true);
    expect(fetchSpy).toHaveBeenCalledWith(
      '/beacon/api/csrf',
      expect.objectContaining({ credentials: 'include' }),
    );
  });
});
