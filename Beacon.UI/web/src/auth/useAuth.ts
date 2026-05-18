import { useQuery } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';

export interface CurrentUser {
  userId: string | null;
  displayName: string | null;
  email: string | null;
  isAuthenticated: boolean;
  roles: string[];
}

export function useAuth() {
  return useQuery<CurrentUser>({
    queryKey: ['auth', 'me'],
    queryFn: async () => (await beaconApi().getCurrentUser()) as unknown as CurrentUser,
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
