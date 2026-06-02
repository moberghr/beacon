import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
import { createSimpleMutation } from '@/lib/mutations';
import type { ApiKeyEntry, CreateApiKeyCommand, CreateApiKeyResult } from '@/api/generated/beacon-api';

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
