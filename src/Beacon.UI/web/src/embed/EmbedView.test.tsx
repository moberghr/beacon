import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { cleanup, render, screen } from '@testing-library/react';
import { mswServer } from '../../vitest.setup';
import { EmbedView } from './EmbedView';
import { EXPIRED_MESSAGE, FRAME_HEADER, FRAME_MARKER_META, readFrameMarker, takeToken } from './embedClient';

/** An unsigned token shaped like Beacon's: the view reads only its `exp`, Beacon verifies the rest. */
function tokenExpiringIn(seconds: number): string {
  const encode = (value: object) =>
    btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode({ sub: 'grant', exp: Math.floor(Date.now() / 1000) + seconds })}.signature`;
}

interface SeenRender {
  authorization: string | null;
  frame: string | null;
  cookie: string | null;
  credentialsMode: RequestCredentials;
}

/** Answers every render with `body` (or `status`), recording what each call sent. */
function recordRenders(answer: { body?: object; status?: number }) {
  const seen: SeenRender[] = [];
  mswServer.use(
    http.post('*/beacon/api/embed/render', ({ request }) => {
      seen.push({
        authorization: request.headers.get('authorization'),
        frame: request.headers.get(FRAME_HEADER),
        cookie: request.headers.get('cookie'),
        credentialsMode: request.credentials,
      });
      return answer.status
        ? HttpResponse.json({ error: 'embed_unavailable' }, { status: answer.status })
        : HttpResponse.json(answer.body);
    }),
  );
  return seen;
}

const result = {
  title: 'Orders by region',
  columns: ['region', 'orders', 'note'],
  rows: [
    { region: 'North', orders: 12, note: null },
    { region: 'South', orders: 7, note: 'late' },
  ],
  truncated: false,
  maxRows: 1000,
};

describe('embed page', () => {
  beforeEach(() => {
    window.history.replaceState(null, '', '/beacon/embed/0123456789abcdef0123456789abcdef');
    document.head.querySelector(`meta[name="${FRAME_MARKER_META}"]`)?.remove();
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('reads the token from the fragment and removes it from the address at once', () => {
    window.history.replaceState(null, '', '/beacon/embed/0123456789abcdef0123456789abcdef?x=1#t=header.payload.sig');

    expect(takeToken()).toBe('header.payload.sig');
    expect(window.location.hash).toBe('');
    expect(window.location.href).not.toContain('header.payload.sig');
    expect(window.location.pathname + window.location.search).toBe('/beacon/embed/0123456789abcdef0123456789abcdef?x=1');
  });

  it('has no token without a fragment', () => {
    expect(takeToken()).toBeNull();
  });

  it('reads the frame marker only from the served meta element', () => {
    expect(readFrameMarker()).toBeNull();

    const meta = document.createElement('meta');
    meta.name = FRAME_MARKER_META;
    meta.content = 'marker-1';
    document.head.appendChild(meta);

    expect(readFrameMarker()).toBe('marker-1');
  });

  it('sends the token only as the BeaconEmbed authorization, with the frame marker and no cookies', async () => {
    const token = tokenExpiringIn(300);
    const seen = recordRenders({ body: result });

    render(<EmbedView token={token} frameMarker="marker-1" host={null} />);

    expect(await screen.findByText('Orders by region')).toBeInTheDocument();
    expect(seen).toEqual([
      { authorization: `BeaconEmbed ${token}`, frame: 'marker-1', cookie: null, credentialsMode: 'omit' },
    ]);
  });

  it('renders the columns and rows', async () => {
    recordRenders({ body: result });

    render(<EmbedView token={tokenExpiringIn(300)} frameMarker="marker-1" host={null} />);

    expect(await screen.findByRole('columnheader', { name: 'region' })).toBeInTheDocument();
    expect(screen.getAllByRole('columnheader').map(x => x.textContent)).toEqual(['region', 'orders', 'note']);
    expect(screen.getByRole('cell', { name: 'North' })).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: 'late' })).toBeInTheDocument();
    expect(screen.getAllByRole('row')).toHaveLength(3);
    expect(screen.queryByText(/Truncated/)).not.toBeInTheDocument();
  });

  it('says where a truncated result was cut', async () => {
    recordRenders({ body: { ...result, truncated: true, maxRows: 2 } });

    render(<EmbedView token={tokenExpiringIn(300)} frameMarker="marker-1" host={null} />);

    expect(await screen.findByText('Truncated at 2 rows.')).toBeInTheDocument();
  });

  it('shows an empty table for an empty result', async () => {
    recordRenders({ body: { ...result, rows: [] } });

    render(<EmbedView token={tokenExpiringIn(300)} frameMarker="marker-1" host={null} />);

    expect(await screen.findByText('No rows')).toBeInTheDocument();
    expect(screen.getAllByRole('columnheader')).toHaveLength(3);
  });

  it('shows the unavailable state and calls nothing without a token', async () => {
    const seen = recordRenders({ body: result });
    const host = { postMessage: vi.fn() };

    render(<EmbedView token={null} frameMarker="marker-1" host={host} />);

    expect(screen.getByText('This view is unavailable')).toBeInTheDocument();
    await new Promise(x => setTimeout(x, 20));
    expect(seen).toHaveLength(0);
    expect(host.postMessage).not.toHaveBeenCalled();
  });

  it('calls nothing without the frame marker', async () => {
    const seen = recordRenders({ body: result });

    render(<EmbedView token={tokenExpiringIn(300)} frameMarker={null} host={null} />);

    expect(screen.getByText('This view is unavailable')).toBeInTheDocument();
    await new Promise(x => setTimeout(x, 20));
    expect(seen).toHaveLength(0);
  });

  it('tells the host page an expired token has expired, without calling Beacon', async () => {
    const seen = recordRenders({ body: result });
    const host = { postMessage: vi.fn() };

    render(<EmbedView token={tokenExpiringIn(-5)} frameMarker="marker-1" host={host} />);

    expect(await screen.findByText('This view has expired')).toBeInTheDocument();
    expect(host.postMessage).toHaveBeenCalledWith(EXPIRED_MESSAGE, '*');
    expect(seen).toHaveLength(0);
  });

  it('shows the generic unavailable state for a refusal of a live token', async () => {
    recordRenders({ status: 401 });
    const host = { postMessage: vi.fn() };

    render(<EmbedView token={tokenExpiringIn(300)} frameMarker="marker-1" host={host} />);

    expect(await screen.findByText('This view is unavailable')).toBeInTheDocument();
    expect(host.postMessage).not.toHaveBeenCalled();
  });

  it('keeps nothing in browser storage', async () => {
    window.history.replaceState(null, '', '/beacon/embed/0123456789abcdef0123456789abcdef#t=header.payload.sig');
    localStorage.clear();
    sessionStorage.clear();
    const cookiesBefore = document.cookie;
    recordRenders({ body: result });

    render(<EmbedView token={takeToken()} frameMarker="marker-1" host={null} />);

    await screen.findByText('Orders by region');
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
    expect(document.cookie).toBe(cookiesBefore);
  });
});
