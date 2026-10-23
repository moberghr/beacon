import { QueryClient, QueryCache, MutationCache } from '@tanstack/react-query';
import { ApiError } from './api';
import { disconnectBeaconHub } from './useHubEvent';
import type { CurrentUser } from '@/auth/useAuth';

export const AUTH_QUERY_KEY = ['auth', 'me'] as const;

export const UNAUTHENTICATED_USER: CurrentUser = {
  userId: null,
  displayName: null,
  email: null,
  isAuthenticated: false,
  roles: [],
  // Not "realtime is off" — this is the logged-out placeholder after a 401, and the real value
  // arrives with the next /auth/me. Left true so signing back in does not have to re-enable it.
  realtimeEnabled: true,
  externalLogin: null,
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
 * `externalLogin` is host configuration, not user state, so it is carried over: dropping it would
 * send an expired session on a host that owns sign-in to Beacon's login page instead of the host's.
 * Every other cached query is dropped (the signed-out user's data must not render for whoever signs
 * in next) and the realtime hub connection, opened for that user, is stopped.
 */
export function createQueryClient(): QueryClient {
  const onError = (error: unknown) => {
    if (error instanceof ApiError && error.status === 401) {
      client.removeQueries({ predicate: (query) => !isAuthQuery(query.queryKey) });
      client.setQueryData<CurrentUser>(AUTH_QUERY_KEY, (previous) => ({
        ...UNAUTHENTICATED_USER,
        externalLogin: previous?.externalLogin ?? null,
      }));
      void disconnectBeaconHub();
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

function isAuthQuery(queryKey: readonly unknown[]): boolean {
  return queryKey.length === AUTH_QUERY_KEY.length && AUTH_QUERY_KEY.every((part, index) => queryKey[index] === part);
}
