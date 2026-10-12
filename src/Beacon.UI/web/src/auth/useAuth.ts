import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import type { ExternalLogin } from './externalLogin';
import { accessOf, type Access, type CurrentPermissions } from './access';

export type { Access, BeaconPermission, CurrentPermissions } from './access';

export interface CurrentUser {
  userId: string | null;
  /** The Beacon user name of the session, when it has one. */
  userName?: string | null;
  displayName: string | null;
  email: string | null;
  isAuthenticated: boolean;
  roles: string[];
  /** Server-advertised: false when the host runs with `Realtime = false`. */
  realtimeEnabled: boolean;
  /** Server-advertised: set when the host owns sign-in, `null` when Beacon's login page is used. */
  externalLogin: ExternalLogin | null;
  /**
   * Server-advertised: this signed-in session can end every other session of its user (Beacon user management is on, and
   * the caller is a session, not an API key). Without it "Sign out everywhere" is not offered.
   */
  signOutEverywhereEnabled?: boolean;
}

export function useAuth() {
  return useQuery<CurrentUser>({
    queryKey: ['auth', 'me'],
    queryFn: async () => unwrap<CurrentUser>(await beaconApi().getCurrentUser()),
  });
}

/**
 * The signed-in user's Beacon permissions. `canRead` is false for an account with no role yet (e.g. a user
 * provisioned by SSO who is waiting for an administrator to assign one) and for one that holds functional roles only.
 * Re-checked when the window regains focus, so a user who was just granted a role gets in without reloading.
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
 * What the signed-in user may do now: read and write, Admin, and the functional permissions held, as the server
 * reports them (an Admin holds every permission). While loading, `isLoading` is true and nothing is held.
 */
export function useAccess(): Access {
  const auth = useAuth();
  const signedIn = auth.data?.isAuthenticated === true;
  const permissions = usePermissions(signedIn);
  const isLoading = auth.isLoading || (signedIn && permissions.isPending);
  const data = signedIn ? permissions.data : undefined;

  return useMemo(() => accessOf(data, isLoading), [data, isLoading]);
}

/**
 * True iff the current user is an Admin now (the server's view, not the session's role claims).
 * Returns `undefined` while the session or its permissions load.
 */
export function useIsAdmin(): boolean | undefined {
  const access = useAccess();
  return access.isLoading ? undefined : access.isAdmin;
}
