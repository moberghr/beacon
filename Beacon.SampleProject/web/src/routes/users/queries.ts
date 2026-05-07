import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

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

function invalidateUsers(qc: ReturnType<typeof useQueryClient>) {
  qc.invalidateQueries({ queryKey: ['users'] });
}

export function useCreateInternalUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: CreateInternalUserPayload) =>
      fetchJson<void>('/beacon/api/users/internal', {
        method: 'POST',
        body: JSON.stringify(payload),
      }),
    onSuccess: () => invalidateUsers(qc),
  });
}

export function useCreateExternalUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (payload: CreateExternalUserPayload) =>
      fetchJson<void>('/beacon/api/users/external', {
        method: 'POST',
        body: JSON.stringify(payload),
      }),
    onSuccess: () => invalidateUsers(qc),
  });
}

export function useUpdateUser() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, payload }: { id: number; payload: UpdateUserPayload }) =>
      fetchJson<void>(`/beacon/api/users/${id}`, {
        method: 'PUT',
        body: JSON.stringify(payload),
      }),
    onSuccess: () => invalidateUsers(qc),
  });
}

export function useToggleUserEnabled() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) =>
      fetchJson<void>(`/beacon/api/users/${id}/toggle-enabled`, { method: 'POST' }),
    onSuccess: () => invalidateUsers(qc),
  });
}
