import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';

// Local strict interfaces bridged via unwrap<T>() — the generated `ApiKeyEntry`
// types date fields as `Date`, but the wire payload deserializes them as
// strings (no reviver). See src/lib/api.ts.
export interface ApiKeyEntry {
  id: number;
  name: string;
  prefix: string;
  scopes: string[];
  createdAt: string;
  lastUsedAt: string | null;
  expiresAt: string | null;
  isActive: boolean;
}

interface GetApiKeysResult {
  entries: ApiKeyEntry[];
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

export function useApiKeysQuery() {
  return useQuery({
    queryKey: KEYS,
    queryFn: async () => unwrap<GetApiKeysResult>(await beaconApi().getApiKeys()),
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
