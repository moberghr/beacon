import { useQuery } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

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
    queryFn: () => fetchJson<CurrentUser>('/beacon/api/auth/me'),
  });
}
