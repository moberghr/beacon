import { QueryClient, QueryCache, MutationCache } from '@tanstack/react-query';
import { ApiError } from './api';
import type { CurrentUser } from '@/auth/useAuth';

export const AUTH_QUERY_KEY = ['auth', 'me'] as const;

export const UNAUTHENTICATED_USER: CurrentUser = {
  userId: null,
  displayName: null,
  email: null,
  isAuthenticated: false,
  roles: [],
};

/**
 * Builds the app QueryClient with a global 401 handler.
 *
 * `beaconFetch` lets 401s bubble up as `ApiError`. Without a central handler, a session that
 * expires mid-app (cookie timeout, server restart, revoked session) surfaces as a per-page
 * "failed to load" error and strands the user — there is no path back to login. Here we reset
 * the cached auth state to unauthenticated on any 401, so <RequireAuth> re-evaluates and
 * redirects to /login via SPA navigation (no full reload). The `/auth/me` query itself is
 * anonymous-tolerant (returns 200 with isAuthenticated:false), so it never 401s and cannot loop.
 */
export function createQueryClient(): QueryClient {
  const onError = (error: unknown) => {
    if (error instanceof ApiError && error.status === 401) {
      client.setQueryData(AUTH_QUERY_KEY, UNAUTHENTICATED_USER);
    }
  };

  const client = new QueryClient({
    queryCache: new QueryCache({ onError }),
    mutationCache: new MutationCache({ onError }),
    defaultOptions: {
      queries: {
        retry: 1,
        staleTime: 30_000,
        refetchOnWindowFocus: false,
      },
    },
  });

  return client;
}
