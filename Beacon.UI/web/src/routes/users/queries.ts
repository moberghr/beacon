import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';
import { createSimpleMutation } from '@/lib/mutations';

export interface UserRoleEntry {
  id: number;
  name: string;
  level: number;
}

export interface UserEntry {
  id: number;
  userName: string;
  email: string | null;
  displayName: string | null;
  isInternalUser: boolean;
  isSuperAdmin: boolean;
  isEnabled: boolean;
  lastLoginAt: string | null;
  roles: UserRoleEntry[];
}

interface GetUsersResult {
  entries: UserEntry[];
}

export interface RoleEntry {
  id: number;
  name: string;
  description: string | null;
  level: number;
  isSystemRole: boolean;
}

interface GetRolesResult {
  entries: RoleEntry[];
}

export interface CreateInternalUserPayload {
  userName: string;
  email: string | null;
  displayName: string | null;
  password: string;
  roleIds: number[];
}

export interface CreateExternalUserPayload {
  externalId: string;
  userName: string;
  email: string | null;
  displayName: string | null;
  roleIds: number[];
}

export interface UpdateUserPayload {
  userName: string;
  email: string | null;
  displayName: string | null;
  isEnabled: boolean;
}

const USERS_KEY = (search: string) => ['users', search] as const;
const USERS_ALL_KEY = ['users'] as const;
const ROLES_KEY = ['users', 'roles'] as const;

export function useUsersQuery(search: string) {
  const params = new URLSearchParams();
  if (search.trim()) params.set('search', search.trim());
  const qs = params.toString();
  return useQuery({
    queryKey: USERS_KEY(search),
    queryFn: () => fetchJson<GetUsersResult>(`/beacon/api/users${qs ? `?${qs}` : ''}`),
  });
}

export function useRolesQuery() {
  return useQuery({
    queryKey: ROLES_KEY,
    queryFn: () => fetchJson<GetRolesResult>('/beacon/api/users/roles'),
  });
}

export function useCreateInternalUser() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateInternalUserPayload, void>({
      qc,
      mutationFn: (payload) =>
        fetchJson<void>('/beacon/api/users/internal', {
          method: 'POST',
          body: JSON.stringify(payload),
        }),
      invalidate: [USERS_ALL_KEY],
      errorFallback: 'Create user failed',
    }),
  );
}

export function useCreateExternalUser() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateExternalUserPayload, void>({
      qc,
      mutationFn: (payload) =>
        fetchJson<void>('/beacon/api/users/external', {
          method: 'POST',
          body: JSON.stringify(payload),
        }),
      invalidate: [USERS_ALL_KEY],
      errorFallback: 'Create user failed',
    }),
  );
}

export function useUpdateUser() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; payload: UpdateUserPayload }, void>({
      qc,
      mutationFn: ({ id, payload }) =>
        fetchJson<void>(`/beacon/api/users/${id}`, {
          method: 'PUT',
          body: JSON.stringify(payload),
        }),
      invalidate: [USERS_ALL_KEY],
      errorFallback: 'Update user failed',
    }),
  );
}

export function useToggleUserEnabled() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) =>
        fetchJson<void>(`/beacon/api/users/${id}/toggle-enabled`, { method: 'POST' }),
      invalidate: [USERS_ALL_KEY],
      errorFallback: 'Toggle user enabled failed',
    }),
  );
}
