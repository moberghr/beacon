import { http, HttpResponse } from 'msw';

/**
 * Default MSW handlers for component tests. Override per-test with
 * `mswServer.use(...)` from vitest.setup.ts.
 */
export const handlers = [
  // Match any origin so we don't have to mirror jsdom's default (currently 'http://localhost:3000').
  http.get('*/beacon/api/projects', () =>
    HttpResponse.json({
      entries: [
        {
          id: 1,
          name: 'Acme Analytics',
          description: 'Sample project',
          dataSourceCount: 3,
          repositoryCount: 1,
          lastScanAt: '2026-05-01T10:00:00Z',
          createdAt: '2026-04-01T10:00:00Z',
        },
        {
          id: 2,
          name: 'Beta Pipeline',
          description: null,
          dataSourceCount: 1,
          repositoryCount: 0,
          lastScanAt: null,
          createdAt: '2026-04-15T10:00:00Z',
        },
      ],
    })
  ),
];
