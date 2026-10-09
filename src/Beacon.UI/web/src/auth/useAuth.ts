import { useQuery } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import type { ExternalLogin } from './externalLogin';

export interface CurrentUser {
  userId: string | null;
  displayName: string | null;
  email: string | null;
  isAuthenticated: boolean;
  roles: string[];
  /** Server-advertised: false when the host runs with `Realtime = false`. */
  realtimeEnabled: boolean;
  /** Server-advertised: set when the host owns sign-in, `null` when Beacon's login page is used. */
  externalLogin: ExternalLogin | null;
}

export function useAuth() {
  return useQuery<CurrentUser>({
    queryKey: ['auth', 'me'],
    queryFn: async () => unwrap<CurrentUser>(await beaconApi().getCurrentUser()),
  });
}

export interface CurrentPermissions {
  canRead: boolean;
  canWrite: boolean;
}

/**
 * The signed-in user's Beacon permissions. `canRead` is false for an account with no role yet (e.g. a user
 * provisioned by SSO who is waiting for an administrator to assign one). Re-checked when the window regains focus,
 * so a user who was just granted a role gets in without reloading.
 */
export function usePermissions(enabled: boolean) {
  return useQuery<CurrentPermissions>({
    queryKey: ['auth', 'permissions'],
    queryFn: async () => unwrap<CurrentPermissions>(await beaconApi().getCurrentPermissions()),
    enabled,
    refetchOnWindowFocus: true,
  });
}

/**
 * True iff the current user is in the `Admin` role (case-insensitive).
 * Returns `undefined` while the auth query is loading.
 */
export function useIsAdmin(): boolean | undefined {
  const { data, isLoading } = useAuth();
  if (isLoading) return undefined;
  if (!data || !data.isAuthenticated) return false;
  return data.roles.some((r) => r.toLowerCase() === 'admin');
}
