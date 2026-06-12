import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { unwrap } from '@/lib/api';
import { beaconApi } from '@/api/client';
import { AiProvider, BedrockAuthMode } from '@/lib/enums';
import { createSimpleMutation } from '@/lib/mutations';

export const AI_PROVIDER_LABEL: Record<number, string> = {
  [AiProvider.OpenAI]: 'OpenAI',
  [AiProvider.Claude]: 'Anthropic',
  [AiProvider.AzureOpenAI]: 'Azure OpenAI',
  [AiProvider.Bedrock]: 'AWS Bedrock',
};

export const BEDROCK_AUTH_MODE_LABEL: Record<number, string> = {
  [BedrockAuthMode.IamRole]: 'IAM role',
  [BedrockAuthMode.AccessKey]: 'Access keys',
  [BedrockAuthMode.TemporaryCredentials]: 'Temporary credentials',
};

export interface AdminSettingsView {
  baseUrl: string | null;
  llmProvider: AiProvider | null;
  llmApiKeySet: boolean;
  llmEndpointSet: boolean;
  llmRegion: string | null;
  llmSessionTokenSet: boolean;
  llmAwsAccessKeyIdSet: boolean;
  llmAwsSecretAccessKeySet: boolean;
  llmBedrockAuthMode: BedrockAuthMode;
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
  llmProvider: AiProvider | null;
  llmModel: string | null;
  llmFastModel: string | null;
  llmRegion: string | null;
  llmBedrockAuthMode: BedrockAuthMode;
  // null = leave unchanged, string = replace
  llmApiKey: string | null;
  llmEndpoint: string | null;
  llmSessionToken: string | null;
  llmAwsAccessKeyId: string | null;
  llmAwsSecretAccessKey: string | null;
  llmMaxConcurrentRequests: number;
  llmTokensPerMinute: number;
  llmRequestsPerMinute: number;
  llmMonthlyBudget: number;
}

export interface TestLlmConnectionPayload {
  llmProvider: AiProvider | null;
  llmModel: string | null;
  llmRegion: string | null;
  llmBedrockAuthMode: BedrockAuthMode;
  llmApiKey: string | null;
  llmEndpoint: string | null;
  llmSessionToken: string | null;
  llmAwsAccessKeyId: string | null;
  llmAwsSecretAccessKey: string | null;
}

export interface TestLlmConnectionResult {
  ok: boolean;
  latencyMs: number | null;
  model: string | null;
  error: string | null;
  sample: string | null;
}

const ADMIN_SETTINGS_KEY = ['admin-settings'] as const;

export function useAdminSettingsQuery() {
  return useQuery({
    queryKey: ADMIN_SETTINGS_KEY,
    queryFn: async () =>
      unwrap<GetAdminSettingsResult>(await beaconApi().getAdminSettings()),
  });
}

export function useUpdateAdminSettings() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<UpdateAdminSettingsPayload, void>({
      qc,
      mutationFn: (values) => beaconApi().updateAdminSettings(values as never),
      invalidate: [ADMIN_SETTINGS_KEY],
      errorFallback: 'Update admin settings failed',
    }),
  );
}

export function useTestLlmConnection() {
  const qc = useQueryClient();
  return useMutation(
    createSimpleMutation<TestLlmConnectionPayload, TestLlmConnectionResult>({
      qc,
      mutationFn: async (values) =>
        unwrap<TestLlmConnectionResult>(await beaconApi().testLlmConnection(values as never)),
      errorFallback: 'Test LLM connection failed',
    }),
  );
}

// ─── Catalogs (presets) ───────────────────────────────────────────────

export interface ModelOption {
  id: string;
  label: string;
  hint?: string;
}

export const OPENAI_MODELS: ModelOption[] = [
  { id: 'gpt-4o', label: 'gpt-4o', hint: 'Default · flagship' },
  { id: 'gpt-4o-mini', label: 'gpt-4o-mini', hint: 'Fast · cheap' },
  { id: 'gpt-4-turbo', label: 'gpt-4-turbo' },
  { id: 'o1-preview', label: 'o1-preview', hint: 'Reasoning' },
  { id: 'o1-mini', label: 'o1-mini', hint: 'Reasoning · fast' },
];

export const ANTHROPIC_MODELS: ModelOption[] = [
  { id: 'claude-opus-4-7', label: 'claude-opus-4-7', hint: 'Default · flagship' },
  { id: 'claude-sonnet-4-6', label: 'claude-sonnet-4-6', hint: 'Balanced' },
  { id: 'claude-haiku-4-5', label: 'claude-haiku-4-5', hint: 'Fast · cheap' },
  { id: 'claude-3-5-sonnet-20241022', label: 'claude-3.5-sonnet (legacy)' },
];

// Latest Bedrock-hosted text/chat models as of 2026-05.
// Anthropic 4.x + Amazon Nova use the EU geo inference profile (eu. prefix) for cross-region
// invocation within the EU geography. DeepSeek and Mistral Large 3 don't expose a geo profile
// and must be invoked in-region (available in eu-north-1 / eu-west-2).
export const BEDROCK_MODELS: ModelOption[] = [
  { id: 'eu.anthropic.claude-opus-4-7', label: 'Claude Opus 4.7', hint: 'Flagship · 1M ctx · adaptive thinking' },
  { id: 'eu.anthropic.claude-opus-4-6-v1', label: 'Claude Opus 4.6', hint: 'Flagship · 1M ctx · long agentic tasks' },
  { id: 'eu.anthropic.claude-sonnet-4-6', label: 'Claude Sonnet 4.6', hint: 'Balanced · 1M ctx · default' },
  { id: 'eu.anthropic.claude-sonnet-4-5-20250929-v1:0', label: 'Claude Sonnet 4.5', hint: 'Prior-gen balanced' },
  { id: 'eu.anthropic.claude-haiku-4-5-20251001-v1:0', label: 'Claude Haiku 4.5', hint: 'Fast · cheap · 200K ctx' },
  { id: 'eu.amazon.nova-2-lite-v1:0', label: 'Amazon Nova 2 Lite', hint: 'Multimodal · cost-efficient · 1M ctx' },
  { id: 'eu.amazon.nova-pro-v1:0', label: 'Amazon Nova Pro', hint: 'Multimodal · balanced · 300K ctx' },
  { id: 'us.meta.llama4-maverick-17b-instruct-v1:0', label: 'Llama 4 Maverick 17B', hint: 'MoE · 1M ctx · US-only geo profile' },
  { id: 'deepseek.v3.2', label: 'DeepSeek V3.2', hint: 'Open MoE · in-region only (eu-north-1, eu-west-2)' },
  { id: 'mistral.mistral-large-3-675b-instruct', label: 'Mistral Large 3', hint: '675B · 256K ctx · in-region only (eu-north-1, eu-west-2)' },
];

export const AWS_REGIONS: ModelOption[] = [
  { id: 'us-east-1', label: 'us-east-1 — N. Virginia' },
  { id: 'us-east-2', label: 'us-east-2 — Ohio' },
  { id: 'us-west-2', label: 'us-west-2 — Oregon' },
  { id: 'eu-west-1', label: 'eu-west-1 — Ireland' },
  { id: 'eu-central-1', label: 'eu-central-1 — Frankfurt' },
  { id: 'ap-southeast-1', label: 'ap-southeast-1 — Singapore' },
  { id: 'ap-northeast-1', label: 'ap-northeast-1 — Tokyo' },
];

export function modelsForProvider(provider: AiProvider | null): ModelOption[] {
  switch (provider) {
    case AiProvider.OpenAI:
      return OPENAI_MODELS;
    case AiProvider.Claude:
      return ANTHROPIC_MODELS;
    case AiProvider.Bedrock:
      return BEDROCK_MODELS;
    case AiProvider.AzureOpenAI:
      return []; // Azure uses deployment names, free-text only
    default:
      return [];
  }
}
