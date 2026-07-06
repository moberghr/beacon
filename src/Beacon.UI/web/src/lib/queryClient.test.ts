import { describe, it, expect } from 'vitest';
import { ApiError } from './api';
import { createQueryClient, AUTH_QUERY_KEY } from './queryClient';

describe('createQueryClient global 401 handler', () => {
  it('resets cached auth state to unauthenticated when a query 401s', async () => {
    const client = createQueryClient();
    client.setQueryData(AUTH_QUERY_KEY, { userId: '1', isAuthenticated: true, roles: [], displayName: 'A', email: null });

    await client
      .fetchQuery({
        queryKey: ['some', 'data'],
        queryFn: async () => {
          throw new ApiError(401, 'Unauthorized');
        },
        retry: false,
      })
      .catch(() => undefined);

    expect(client.getQueryData(AUTH_QUERY_KEY)).toMatchObject({ isAuthenticated: false });
  });

  it('leaves auth state untouched on non-401 errors', async () => {
    const client = createQueryClient();
    client.setQueryData(AUTH_QUERY_KEY, { userId: '1', isAuthenticated: true, roles: [], displayName: 'A', email: null });

    await client
      .fetchQuery({
        queryKey: ['some', 'data'],
        queryFn: async () => {
          throw new ApiError(500, 'Server error');
        },
        retry: false,
      })
      .catch(() => undefined);

    expect(client.getQueryData(AUTH_QUERY_KEY)).toMatchObject({ isAuthenticated: true });
  });
});
