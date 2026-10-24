import { beforeEach, describe, expect, it, vi } from 'vitest';

const loaded = vi.hoisted(() => ({ names: [] as string[] }));

vi.mock('./routes/queries/QueryDetailPage', () => {
  loaded.names.push('QueryDetailPage');
  return { default: () => null };
});
vi.mock('./routes/queries/QueriesListPage', () => {
  loaded.names.push('QueriesListPage');
  return { default: () => null };
});

import { prefetchRouteChunk } from './routeChunks';

describe('prefetchRouteChunk', () => {
  beforeEach(() => {
    loaded.names.length = 0;
  });

  it('loads the chunk of the page the app was opened on, behind the router basename', async () => {
    prefetchRouteChunk('/beacon/queries/42');
    await vi.waitFor(() => expect(loaded.names).toEqual(['QueryDetailPage']));
  });

  it('matches the list route exactly, not the detail route', async () => {
    prefetchRouteChunk('/beacon/queries');
    await vi.waitFor(() => expect(loaded.names).toEqual(['QueriesListPage']));
  });

  it('does nothing for an unknown path', async () => {
    prefetchRouteChunk('/beacon/nope');
    await Promise.resolve();
    expect(loaded.names).toEqual([]);
  });
});
