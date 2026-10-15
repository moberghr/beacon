import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';
import { OwnedResourceKind } from '@/lib/enums';
import type { PagedList } from '@/lib/paging';

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

export interface RoleEntry {
  id: number;
  name: string;
  description: string | null;
  level: number;
  isSystemRole: boolean;
  /** The functional permission a functional role grants (e.g. `Auditor`); null for Admin, Editor, Viewer and others. */
  permission?: string | null;
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

/** A resource a user owns, created or changed last (times as ISO strings). */
export interface OwnedResource {
  id: number;
  kind: OwnedResourceKind;
  name: string;
  /** Its owner: scheduled runs act with the owner's access. Recipients and migration jobs have no owner. */
  isOwner: boolean;
  isCreator: boolean;
  isLastModifier: boolean;
  /** True for a resource in a project the caller cannot read: listed by id and kind, its name empty. */
  nameWithheld: boolean;
  createdTime: string;
  modifiedTime: string | null;
}

export const OWNED_RESOURCES_PAGE_SIZE = 10;

const USERS_ALL_KEY = ['users'] as const;
const ROLES_KEY = ['users', 'roles'] as const;

/** The users grid: server-paged, alphabetical by user name, search in the URL. */
export function useUsersList() {
  return usePagedList<UserEntry, { search: string }>({
    queryKey: USERS_ALL_KEY,
    path: '/beacon/api/users',
    filters: { search: '' },
    defaultSort: { column: 'userName', direction: 'asc' },
  });
}

export function useRolesQuery() {
  return useQuery({
    queryKey: ROLES_KEY,
    queryFn: async () => unwrap<GetRolesResult>(await beaconApi().getRoles()),
  });
}

export function useCreateInternalUser() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateInternalUserPayload, void>({
      qc,
      mutationFn: (payload) => beaconApi().createInternalUser(payload as never),
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
      mutationFn: (payload) => beaconApi().createExternalUser(payload as never),
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
      mutationFn: ({ id, payload }) => beaconApi().updateUser(id, payload as never),
      invalidate: [USERS_ALL_KEY],
      errorFallback: 'Update user failed',
    }),
  );
}

/**
 * Replaces the user's roles (`PUT /users/{id}/roles`, the complete set). Not toasted: the dialog shows the server's
 * reason (400 separation of duties or an unknown role, 403 a change the caller may not make) beside the roles.
 */
export function useSetUserRoles() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<{ id: number; roleIds: number[] }, void>({
      qc,
      mutationFn: ({ id, roleIds }) => beaconApi().setUserRoles(id, { roleIds }),
      invalidate: [USERS_ALL_KEY],
      errorFallback: 'Saving the roles failed',
      errorToast: false,
    }),
  );
}

export function useToggleUserEnabled() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) => beaconApi().toggleUserEnabled(id),
      invalidate: [USERS_ALL_KEY],
      errorFallback: 'Toggle user enabled failed',
    }),
  );
}

/**
 * Signs out every session of the user (`POST /users/{id}/sign-out-everywhere`): their security stamp changes, so each
 * session ends at its next check against the user store. Their API keys keep working.
 */
export function useSignUserOutEverywhere() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, void>({
      qc,
      mutationFn: (id) => beaconApi().signUserOutEverywhere(id),
      invalidate: [USERS_ALL_KEY],
      errorFallback: 'Signing the user out failed',
    }),
  );
}

/**
 * One page of the live resources of `kind` the user owns, created or changed last (oldest first), for an Auditor or a
 * UserAdmin; read only while `enabled`.
 */
export function useOwnedResources(userId: number, kind: OwnedResourceKind, page: number, enabled: boolean) {
  return useQuery({
    queryKey: [...USERS_ALL_KEY, userId, 'owned-resources', kind, page],
    queryFn: async () =>
      unwrap<PagedList<OwnedResource>>(
        await beaconApi().getUserOwnedResources(userId, kind, page, OWNED_RESOURCES_PAGE_SIZE, undefined),
      ),
    enabled,
    placeholderData: keepPreviousData,
  });
}
