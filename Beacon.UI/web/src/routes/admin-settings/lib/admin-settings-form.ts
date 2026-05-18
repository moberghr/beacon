import {
  AiProvider,
  BedrockAuthMode,
  type AdminSettingsView,
  type AiProviderId,
  type BedrockAuthModeId,
  type UpdateAdminSettingsPayload,
  type TestLlmConnectionPayload,
} from '../queries';

/**
 * Form shape used by `AdminSettingsForm`. Mirrors `AdminSettingsView` but
 * with strings for every field (RHF + zod prefer plain strings; `null`
 * round-trips through the conversion helpers below).
 */
/**
 * Mirrors the zod schema in `AdminSettingsPage` — `llmProvider` and
 * `llmBedrockAuthMode` are validated as `z.number()`, so they come out
 * of zod as plain `number` (we cast back to the branded enum types in
 * the payload converters below).
 */
export interface AdminSettingsFormValues {
  baseUrl?: string;
  llmProvider: number;
  llmModel?: string;
  llmFastModel?: string;
  llmRegion?: string;
  llmBedrockAuthMode: number;
  llmApiKey?: string;
  llmEndpoint?: string;
  llmSessionToken?: string;
  llmAwsAccessKeyId?: string;
  llmAwsSecretAccessKey?: string;
  llmMaxConcurrentRequests: number;
  llmTokensPerMinute: number;
  llmRequestsPerMinute: number;
  llmMonthlyBudget: number;
}

/**
 * Trim and treat empty strings as `null`. Used for non-secret string
 * fields where the backend distinguishes "unset" from "empty".
 */
export function emptyToNull(value: string | undefined | null): string | null {
  if (value === undefined || value === null) return null;
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * Like `emptyToNull` but does NOT trim — secret fields can legitimately
 * contain leading/trailing whitespace and the backend treats `null` as
 * "leave unchanged" rather than "clear".
 */
export function secretToNull(value: string | undefined | null): string | null {
  if (value === undefined || value === null) return null;
  return value.length === 0 ? null : value;
}

/**
 * Pull the loaded `AdminSettingsView` (or the unloaded default) into the
 * form shape. Secret fields are always blanked on load — the backend
 * only tells us whether one is set, never the value.
 */
export function settingsToForm(s: AdminSettingsView | undefined): AdminSettingsFormValues {
  if (s === undefined) {
    return {
      baseUrl: '',
      llmProvider: AiProvider.OpenAI,
      llmModel: '',
      llmFastModel: '',
      llmRegion: '',
      llmBedrockAuthMode: BedrockAuthMode.IamRole,
      llmApiKey: '',
      llmEndpoint: '',
      llmSessionToken: '',
      llmAwsAccessKeyId: '',
      llmAwsSecretAccessKey: '',
      llmMaxConcurrentRequests: 5,
      llmTokensPerMinute: 0,
      llmRequestsPerMinute: 0,
      llmMonthlyBudget: 0,
    };
  }
  return {
    baseUrl: s.baseUrl ?? '',
    llmProvider: (s.llmProvider ?? AiProvider.OpenAI) as AiProviderId,
    llmModel: s.llmModel ?? '',
    llmFastModel: s.llmFastModel ?? '',
    llmRegion: s.llmRegion ?? '',
    llmBedrockAuthMode: s.llmBedrockAuthMode,
    llmApiKey: '',
    llmEndpoint: '',
    llmSessionToken: '',
    llmAwsAccessKeyId: '',
    llmAwsSecretAccessKey: '',
    llmMaxConcurrentRequests: s.llmMaxConcurrentRequests,
    llmTokensPerMinute: s.llmTokensPerMinute,
    llmRequestsPerMinute: s.llmRequestsPerMinute,
    llmMonthlyBudget: s.llmMonthlyBudget,
  };
}

/**
 * Convert form values to the update-payload shape the backend expects.
 */
export function formToUpdatePayload(values: AdminSettingsFormValues): UpdateAdminSettingsPayload {
  return {
    baseUrl: emptyToNull(values.baseUrl),
    llmProvider: values.llmProvider as AiProviderId,
    llmModel: emptyToNull(values.llmModel),
    llmFastModel: emptyToNull(values.llmFastModel),
    llmRegion: emptyToNull(values.llmRegion),
    llmBedrockAuthMode: values.llmBedrockAuthMode as BedrockAuthModeId,
    llmApiKey: secretToNull(values.llmApiKey),
    llmEndpoint: emptyToNull(values.llmEndpoint),
    llmSessionToken: secretToNull(values.llmSessionToken),
    llmAwsAccessKeyId: secretToNull(values.llmAwsAccessKeyId),
    llmAwsSecretAccessKey: secretToNull(values.llmAwsSecretAccessKey),
    llmMaxConcurrentRequests: values.llmMaxConcurrentRequests,
    llmTokensPerMinute: values.llmTokensPerMinute,
    llmRequestsPerMinute: values.llmRequestsPerMinute,
    llmMonthlyBudget: values.llmMonthlyBudget,
  };
}

/**
 * Convert form values to the test-connection payload shape (subset of
 * the update payload — only fields the connection test needs).
 */
export function formToTestPayload(values: AdminSettingsFormValues): TestLlmConnectionPayload {
  return {
    llmProvider: values.llmProvider as AiProviderId,
    llmModel: emptyToNull(values.llmModel),
    llmRegion: emptyToNull(values.llmRegion),
    llmBedrockAuthMode: values.llmBedrockAuthMode as BedrockAuthModeId,
    llmApiKey: secretToNull(values.llmApiKey),
    llmEndpoint: emptyToNull(values.llmEndpoint),
    llmSessionToken: secretToNull(values.llmSessionToken),
    llmAwsAccessKeyId: secretToNull(values.llmAwsAccessKeyId),
    llmAwsSecretAccessKey: secretToNull(values.llmAwsSecretAccessKey),
  };
}
