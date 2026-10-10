import { useMutation, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { usePagedList } from '@/lib/usePagedList';
import { createSimpleMutation } from '@/lib/mutations';

// Local strict interfaces bridged via unwrap<T>() — the generated `ApiKeyEntry`
// types date fields as `Date`, but the wire payload deserializes them as
// strings (no reviver). See src/lib/api.ts.
export interface ApiKeyEntry {
  id: number;
  name: string;
  prefix: string;
  /** The scopes the key grants: `Read` and/or `Execute`. */
  scopes: string[];
  /** `null` when the key is not restricted to projects. */
  allowedProjectIds: number[] | null;
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
  isActive: boolean;
}

/** A key with its owner, as an administrator sees it. */
export interface AdminApiKeyEntry extends ApiKeyEntry {
  revokedAt: string | null;
  userId: number | null;
  userName: string | null;
}

export interface CreateApiKeyPayload {
  name: string;
  scopes: string[];
  allowedProjectIds: number[] | null;
  expiresAt: Date | null;
}

export interface CreateApiKeyResult {
  plainTextKey: string;
}

const KEYS = ['api-keys'] as const;
const ADMIN_KEYS = ['api-keys', 'admin'] as const;

/** The caller's API keys: server-paged, newest first. */
export function useApiKeysList() {
  return usePagedList<ApiKeyEntry>({
    queryKey: KEYS,
    path: '/beacon/api/api-keys',
    defaultSort: { column: 'createdAt', direction: 'desc' },
  });
}

/**
 * Every user's API keys, for administrators: server-paged, newest first, optionally one user's
 * (`allUserId` in the URL). Its URL keys are prefixed `all` so it shares the page with the caller's own list.
 */
export function useAllApiKeysList(enabled: boolean) {
  return usePagedList<AdminApiKeyEntry, { userId: string }>({
    queryKey: ADMIN_KEYS,
    path: '/beacon/api/api-keys/admin',
    defaultSort: { column: 'createdAt', direction: 'desc' },
    filters: { userId: '' },
    urlPrefix: 'all',
    enabled,
  });
}

export function useCreateApiKey() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateApiKeyPayload, CreateApiKeyResult>({
      qc,
      mutationFn: async (body) => unwrap<CreateApiKeyResult>(await beaconApi().createApiKey(body)),
      invalidate: [KEYS],
      errorFallback: 'Create API key failed',
    }),
  );
}

export function useRevokeApiKey() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, unknown>({
      qc,
      mutationFn: (id) => beaconApi().revokeApiKey(id),
      invalidate: [KEYS],
      errorFallback: 'Revoke API key failed',
    }),
  );
}

/** Revokes any user's key (administrators). */
export function useAdminRevokeApiKey() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<number, unknown>({
      qc,
      mutationFn: (id) => beaconApi().adminRevokeApiKey(id),
      invalidate: [KEYS],
      errorFallback: 'Revoke API key failed',
    }),
  );
}
