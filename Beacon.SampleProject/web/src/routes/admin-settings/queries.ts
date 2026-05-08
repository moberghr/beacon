import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { fetchJson } from '@/lib/api';

// NOTE: Phase 3 Batch 4 — hand-typed wrappers; replace with `beaconApi()` after
// `npm run codegen`. Mirrors `Beacon.Core.Handlers.AdminSettings.*Handler`.

// AiProvider enum, must mirror Beacon.Core.Data.Enums.AiProvider.
export const AiProvider = {
  OpenAI: 0,
  Claude: 1,
  AzureOpenAI: 2,
  Bedrock: 3,
} as const;
export type AiProviderId = (typeof AiProvider)[keyof typeof AiProvider];

export const AI_PROVIDER_LABEL: Record<number, string> = {
  0: 'OpenAI',
  1: 'Claude',
  2: 'Azure OpenAI',
  3: 'AWS Bedrock',
};

export interface AdminSettingsView {
  baseUrl: string | null;
  llmProvider: AiProviderId | null;
  llmApiKeySet: boolean;
  llmEndpointSet: boolean;
  llmRegion: string | null;
  llmSessionTokenSet: boolean;
  llmModel: string | null;
  llmFastModel: string | null;
  llmMaxConcurrentRequests: number;
  llmTokensPerMinute: number;
  llmRequestsPerMinute: number;
  llmMonthlyBudget: number;
}

export interface AdminSettingHistoryEntry {
  settingKey: string;
  oldValue: string | null;
  newValue: string | null;
  changedAt: string;
  changedByUserId: string | null;
}

interface GetAdminSettingsResult {
  settings: AdminSettingsView;
  history: AdminSettingHistoryEntry[];
}

export interface UpdateAdminSettingsPayload {
  baseUrl: string | null;
  llmProvider: AiProviderId | null;
  llmModel: string | null;
  llmFastModel: string | null;
  llmRegion: string | null;
  // null = leave unchanged, string = replace
  llmApiKey: string | null;
  llmEndpoint: string | null;
  llmSessionToken: string | null;
  llmMaxConcurrentRequests: number;
  llmTokensPerMinute: number;
  llmRequestsPerMinute: number;
  llmMonthlyBudget: number;
}

const ADMIN_SETTINGS_KEY = ['admin-settings'] as const;

export function useAdminSettingsQuery() {
  return useQuery({
    queryKey: ADMIN_SETTINGS_KEY,
    queryFn: () => fetchJson<GetAdminSettingsResult>('/beacon/api/admin-settings'),
  });
}

export function useUpdateAdminSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (values: UpdateAdminSettingsPayload) =>
      fetchJson<void>('/beacon/api/admin-settings', {
        method: 'PUT',
        body: JSON.stringify(values),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ADMIN_SETTINGS_KEY }),
  });
}
