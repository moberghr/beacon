import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';
import type { ApiKeyEntry, CreateApiKeyCommand, CreateApiKeyResult } from '@/api/generated/beacon-api';

// FOLLOW-UP (unwrap<T>() boundary cleanup): the generated `ApiKeyEntry` types
// date fields as `Date`, but the wire payload deserializes them as strings (no
// reviver). The accurate fix is a local strict interface (string dates) bridged
// via `unwrap<T>()` at the queryFn — see `routes/mcp/queries.ts` for the pattern.
// Deferred here (and in approvals/, migration-jobs/) to avoid a wide type cascade.
export type { ApiKeyEntry, CreateApiKeyResult };

const KEYS = ['api-keys'] as const;

export function useApiKeysQuery() {
  return useQuery({
    queryKey: KEYS,
    queryFn: () => beaconApi().getApiKeys(),
  });
}

export function useCreateApiKey() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<CreateApiKeyCommand, CreateApiKeyResult>({
      qc,
      mutationFn: (body) => beaconApi().createApiKey(body),
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
