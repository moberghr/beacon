import { type ReactNode } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import { http, HttpResponse } from 'msw';
import { mswServer } from '../../../vitest.setup';
import { useDataSourceCount } from './queries';

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe('useDataSourceCount', () => {
  it.each([
    [{ migrationTargets: true }, 'true', 2],
    [{ migrationTargets: false }, 'false', 5],
  ])('counts with %o, one row fetched, the filter sent to the server', async (options, sent, expected) => {
    const asked: URLSearchParams[] = [];
    mswServer.use(
      http.get('*/beacon/api/data-sources', ({ request }) => {
        const params = new URL(request.url).searchParams;
        asked.push(params);
        const total = params.get('migrationTargets') === 'true' ? 2 : 5;
        return HttpResponse.json({ totalCount: total, pageCount: total, items: [] });
      }),
    );

    const { result } = renderHook(() => useDataSourceCount(options), { wrapper });

    await waitFor(() => expect(result.current.data).toBe(expected));
    expect(asked).toHaveLength(1);
    expect(asked[0].get('migrationTargets')).toBe(sent);
    expect(asked[0].get('pageSize')).toBe('1');
  });

  it('sends no migration filter when none is asked for', async () => {
    const asked: URLSearchParams[] = [];
    mswServer.use(
      http.get('*/beacon/api/data-sources', ({ request }) => {
        asked.push(new URL(request.url).searchParams);
        return HttpResponse.json({ totalCount: 7, pageCount: 7, items: [] });
      }),
    );

    const { result } = renderHook(() => useDataSourceCount({ databaseOnly: true }), { wrapper });

    await waitFor(() => expect(result.current.data).toBe(7));
    expect(asked[0].has('migrationTargets')).toBe(false);
    expect(asked[0].get('databaseOnly')).toBe('true');
  });
});
