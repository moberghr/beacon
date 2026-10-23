import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ApiError } from './api';
import { createQueryClient, AUTH_QUERY_KEY } from './queryClient';

const disconnectBeaconHub = vi.fn(() => Promise.resolve());
vi.mock('./useHubEvent', () => ({
  disconnectBeaconHub: () => disconnectBeaconHub(),
}));

beforeEach(() => {
  disconnectBeaconHub.mockClear();
});

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

  it('drops every other cached query and stops the realtime connection on a 401', async () => {
    const client = createQueryClient();
    client.setQueryData(AUTH_QUERY_KEY, { userId: '1', isAuthenticated: true, roles: [], displayName: 'A', email: null });
    client.setQueryData(['projects'], [{ id: 1, name: 'Previous user\'s project' }]);

    await client
      .fetchQuery({
        queryKey: ['some', 'data'],
        queryFn: async () => {
          throw new ApiError(401, 'Unauthorized');
        },
        retry: false,
      })
      .catch(() => undefined);

    expect(client.getQueryData(['projects'])).toBeUndefined();
    expect(client.getQueryCache().findAll().map((query) => query.queryKey)).toEqual([AUTH_QUERY_KEY]);
    expect(client.getQueryData(AUTH_QUERY_KEY)).toMatchObject({ isAuthenticated: false });
    expect(disconnectBeaconHub).toHaveBeenCalledTimes(1);
  });

  it('keeps the cache and the connection on non-401 errors', async () => {
    const client = createQueryClient();
    client.setQueryData(['projects'], [{ id: 1 }]);

    await client
      .fetchQuery({
        queryKey: ['some', 'data'],
        queryFn: async () => {
          throw new ApiError(500, 'Server error');
        },
        retry: false,
      })
      .catch(() => undefined);

    expect(client.getQueryData(['projects'])).toEqual([{ id: 1 }]);
    expect(disconnectBeaconHub).not.toHaveBeenCalled();
  });
});
