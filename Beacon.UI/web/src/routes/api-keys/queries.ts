import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { beaconApi } from '@/api/client';
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
  return useMutation({
    mutationFn: (body: CreateApiKeyCommand) => beaconApi().createApiKey(body),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS }),
  });
}

export function useRevokeApiKey() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => beaconApi().revokeApiKey(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS }),
  });
}
